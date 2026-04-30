using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Manages a persistent Docker named volume that stores the sandbox's own
/// Azure CLI profile. We cannot reuse the host's MSAL token cache directly
/// because on Windows it is encrypted with DPAPI (user-specific) and is
/// undecipherable from inside a Linux container. Instead we keep a Linux-
/// native profile inside a named volume that survives across container runs.
///
/// The first deployment triggers an 'az login --use-device-code' (the code is
/// streamed to the live log). From that point on every deploy re-uses the
/// token cache stored in the volume.
/// </summary>
public static class SandboxAzureAuth
{
    public const string VolumeName = "agentichub-azure-profile";

    /// <summary>
    /// Second persistent named volume that stores the sandbox's Docker
    /// CLI config (<c>~/.docker/config.json</c>). Without this volume each
    /// step runs in a fresh ephemeral container and any <c>az acr login</c>
    /// run during step N is gone by step N+1, so a follow-up
    /// <c>azd deploy</c> / <c>docker push</c> hits the registry with no
    /// auth and fails with <c>denied: authentication required</c>. Mounting
    /// the same volume across all steps means <c>az acr login</c> only has
    /// to run once per registry per session and stays valid for every
    /// subsequent push (the token's lifetime is several hours).
    /// </summary>
    public const string DockerConfigVolumeName = "agentichub-docker-config";

    private static readonly SemaphoreSlim _sem = new(1, 1);
    private static bool _verified;

    /// <summary>
    /// Ensures the named volume exists and that the sandbox has a valid Azure
    /// login. If it is the first run (or the token expired), triggers a device
    /// code login inside the container and streams progress to the provided
    /// logger. Subsequent calls are no-ops.
    /// </summary>
    /// <param name="tenantId">
    /// Optional Azure AD tenant id. When provided we (a) PIN the device-code
    /// login to this tenant via <c>az login --tenant &lt;id&gt;</c>, and (b)
    /// validate that any pre-existing cached profile in the volume is bound
    /// to the SAME tenant; if not, we wipe the cache and re-login. This is
    /// what lets the UI pre-specify the tenant and avoid the "wrong default
    /// tenant" loop.
    /// </param>
    /// <param name="subscriptionId">
    /// Optional Azure subscription id. After a successful login we run
    /// <c>az account set --subscription &lt;id&gt;</c> so every later step
    /// targets this subscription, even when the tenant has many.
    /// </param>
    public static async Task EnsureAsync(
        string sandboxImage,
        string? tenantId,
        string? subscriptionId,
        Action<string, string> log,
        CancellationToken ct)
    {
        await _sem.WaitAsync(ct);
        try
        {
            // The static "_verified" cache is keyed by process lifetime, not
            // by (tenant, subscription) tuple. If the user retries a deploy
            // after switching identity in the UI we MUST re-validate, so we
            // never short-circuit when the caller passed an explicit tenant
            // or subscription. The fast path stays for the common
            // "no UI override" case.
            if (_verified && string.IsNullOrWhiteSpace(tenantId)
                          && string.IsNullOrWhiteSpace(subscriptionId)) return;

            await EnsureVolumeExistsAsync(VolumeName, ct);
            // Same defensive logic for the docker-config volume so every
            // sandbox step shares one ~/.docker/config.json. Failure here
            // is non-fatal: docker push will just need an `az acr login`
            // every step (slower, but correctness-equivalent).
            await EnsureVolumeExistsAsync(DockerConfigVolumeName, ct);

            // Probe: is the volume already authenticated, AND (when the
            // user pinned a tenant) for the SAME tenant? We run a single
            // 'az account show' and capture stdout so we can compare the
            // tenantId field. Any mismatch -> force re-login. This is
            // the central fix for the "tenant loop" the user reported:
            // before, a stale cache from a previous deploy on a
            // different tenant would silently win and azd would deploy
            // into the wrong directory.
            var probeOut = new System.Text.StringBuilder();
            var probeOk = await RunInContainerAsync(
                sandboxImage,
                "az account show --only-show-errors -o json",
                line => { lock (probeOut) probeOut.AppendLine(line); },
                ct);

            string? cachedTenant = null;
            string? cachedSub = null;
            if (probeOk)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(probeOut.ToString());
                    if (doc.RootElement.TryGetProperty("tenantId", out var t))
                        cachedTenant = t.GetString();
                    if (doc.RootElement.TryGetProperty("id", out var i))
                        cachedSub = i.GetString();
                }
                catch { /* malformed json -> treat as not authenticated */ }
            }

            var tenantMismatch = !string.IsNullOrWhiteSpace(tenantId)
                && !string.IsNullOrWhiteSpace(cachedTenant)
                && !string.Equals(tenantId, cachedTenant, StringComparison.OrdinalIgnoreCase);

            if (probeOk && !tenantMismatch)
            {
                log("info",
                    $"Sandbox Azure profile already authenticated " +
                    $"(tenant={cachedTenant ?? "?"}); reusing cached credentials.");
            }
            else
            {
                if (tenantMismatch)
                {
                    log("status",
                        $"Cached Azure login is for tenant '{cachedTenant}' but the " +
                        $"deploy was pinned to tenant '{tenantId}'. Wiping the cached " +
                        "profile and starting a fresh device-code login.");
                    // 'az logout' is the supported way to clear MSAL cache;
                    // fallback to deleting the cache files if it fails (the
                    // volume content is owned by root inside the container).
                    await RunInContainerAsync(
                        sandboxImage,
                        "az logout --only-show-errors 2>/dev/null; " +
                        "rm -rf /root/.azure/msal_token_cache.* /root/.azure/azureProfile.json " +
                        "/root/.azure/accessTokens.json /root/.azure/service_principal_entries.json " +
                        "2>/dev/null; true",
                        _ => { }, ct);
                }
                else
                {
                    log("status",
                        "Sandbox has no cached Azure credentials. Starting device code login...");
                }
                log("info", "==> Copy the code below, open the URL, and approve the sign-in.");

                var tenantArg = string.IsNullOrWhiteSpace(tenantId) ? "" : $"--tenant {tenantId} ";
                var loginCmd = $"az login --use-device-code {tenantArg}--only-show-errors";
                var loginOk = await RunInContainerAsync(
                    sandboxImage, loginCmd,
                    line => log("info", line), ct);

                if (!loginOk)
                    throw new InvalidOperationException(
                        "Device code login failed inside the sandbox. " +
                        "Check the log above for the device code URL and retry.");
            }

            // Pin the active subscription:
            //   1. Explicit user choice wins (s.SubscriptionId from UI).
            //   2. Otherwise, when a tenant was pinned, fall back to the
            //      first subscription in that tenant (mirrors old behaviour).
            //   3. No-op when neither is set � 'az' / 'azd' use their
            //      own default discovery.
            if (!string.IsNullOrWhiteSpace(subscriptionId))
            {
                var setOk = await RunInContainerAsync(
                    sandboxImage,
                    $"az account set --subscription \"{subscriptionId}\" --only-show-errors",
                    line => log("info", line), ct);
                if (setOk)
                    log("info", $"Active subscription pinned to {subscriptionId}.");
                else
                    log("warn",
                        $"Could not pin active subscription to '{subscriptionId}'. " +
                        "The id may be invalid for this tenant or the user lacks " +
                        "access. Continuing with the default subscription of the " +
                        "logged-in identity.");
            }
            else if (!string.IsNullOrWhiteSpace(tenantId))
            {
                await RunInContainerAsync(
                    sandboxImage,
                    $"az account set --subscription $(az account list --query \"[?tenantId=='{tenantId}'] | [0].id\" -o tsv)",
                    _ => { }, ct);
            }

            log("info", "Sandbox Azure login completed. Credentials will be reused on next deploys.");
            _verified = true;
        }
        finally
        {
            _sem.Release();
        }

        // Pre-flight RBAC diagnostic: check whether the signed-in
        // identity has permission to create role assignments on the
        // pinned subscription. Most azd templates create role
        // assignments via Bicep, so without this permission `azd
        // provision` fails 5-10 minutes into the deploy with an opaque
        // AuthorizationFailed. Surfacing this NOW saves the user a
        // long round-trip.
        //
        // Three-step probe:
        //   1. Resolve the current principal's objectId (via 'az ad
        //      signed-in-user show' or 'az account show').
        //   2. Enumerate role assignments scoped at /subscriptions/<id>.
        //   3. If any of {Owner, User Access Administrator, Role Based
        //      Access Control Administrator} is present, all good.
        //      Otherwise, attempt a SELF-GRANT of 'User Access
        //      Administrator' (only succeeds when the principal is
        //      already Owner) -- this is the single most common gap on
        //      personal MCAPS subs where the user IS the Owner but the
        //      cached token predates a recent grant. If that also fails,
        //      log a clear actionable warning naming the missing role
        //      and the EXACT az command an admin must run.
        if (!string.IsNullOrWhiteSpace(subscriptionId))
        {
            await PreflightRbacCheckAsync(
                sandboxImage, subscriptionId!, log, ct);
        }
    }

    /// <summary>
    /// Probe RBAC on the pinned subscription and, when possible, self-
    /// grant 'User Access Administrator' so role-assignment-creating
    /// templates can validate. Best-effort: failures are logged as
    /// warnings � the actual `azd provision` will surface the real
    /// error if the gap remains, but at least the user already has the
    /// fix instructions in their log by then.
    /// </summary>
    private static async Task PreflightRbacCheckAsync(
        string sandboxImage,
        string subscriptionId,
        Action<string, string> log,
        CancellationToken ct)
    {
        // Probe outline:
        //   1. Resolve principal objectId (Graph /me first; fall back to
        //      'az account show' which also covers tokens that lack the
        //      Graph User.Read scope).
        //   2. List role assignments at /subscriptions/<id>.
        //   3. List role assignments anywhere under that subscription
        //      (resource-group / resource scope) so we can detect the
        //      "narrow guest" case (only Cognitive Services User on a
        //      single account, etc.) and produce a precise message.
        //   4. If a role-assignment-capable role exists at sub scope =>
        //      OK. Otherwise attempt self-grant of UAA (works only when
        //      the principal already is sub Owner). On failure, surface
        //      the *correct* remediation depending on what we observed.
        var script =
            "set +e; " +
            "OBJ=$(az ad signed-in-user show --query id -o tsv 2>/dev/null); " +
            "if [ -z \"$OBJ\" ]; then " +
            "  OBJ=$(az account show --query user.name -o tsv 2>/dev/null); " +
            "fi; " +
            "UPN=$(az account show --query user.name -o tsv 2>/dev/null); " +
            "if [ -z \"$OBJ\" ]; then " +
            "  echo 'PREFLIGHT_RBAC: could not resolve current principal'; exit 0; " +
            "fi; " +
            "echo \"PREFLIGHT_RBAC: principal=$OBJ\"; " +
            "echo \"PREFLIGHT_RBAC: upn=$UPN\"; " +
            $"ROLES=$(az role assignment list --assignee \"$OBJ\" " +
            $"  --scope /subscriptions/{subscriptionId} " +
            "  --include-inherited --include-groups " +
            "  --query \"[].roleDefinitionName\" -o tsv 2>/dev/null); " +
            "echo \"PREFLIGHT_RBAC: roles=$(echo $ROLES | tr '\\n' ',' | sed 's/,$//')\"; " +
            // Count narrow assignments anywhere under the sub so we can
            // tell "you are a guest with 2 narrow roles on Foundry" from
            // "you have absolutely nothing on this tenant".
            $"NARROW=$(az role assignment list --assignee \"$OBJ\" --all " +
            "  --include-inherited --include-groups " +
            $"  --query \"[?contains(scope, '/subscriptions/{subscriptionId}')].roleDefinitionName\" " +
            "  -o tsv 2>/dev/null | wc -l); " +
            "echo \"PREFLIGHT_RBAC: narrow_role_count=$NARROW\"; " +
            "if echo \"$ROLES\" | grep -qiE '^(Owner|User Access Administrator|Role Based Access Control Administrator)$'; then " +
            "  echo 'PREFLIGHT_RBAC: OK_HAS_RBAC_WRITE'; exit 0; " +
            "fi; " +
            "echo 'PREFLIGHT_RBAC: missing roleAssignments/write capable role; attempting self-grant'; " +
            $"GRANT_ERR=$(az role assignment create --assignee \"$OBJ\" " +
            "  --role 'User Access Administrator' " +
            $"  --scope /subscriptions/{subscriptionId} " +
            "  --only-show-errors 2>&1); " +
            "GRANT_RC=$?; " +
            "if [ $GRANT_RC -eq 0 ]; then " +
            "  echo 'PREFLIGHT_RBAC: SELF_GRANT_OK'; " +
            "else " +
            "  echo 'PREFLIGHT_RBAC: SELF_GRANT_FAILED'; " +
            "  echo \"PREFLIGHT_RBAC: grant_err=$(echo $GRANT_ERR | tr '\\n' ' ' | head -c 400)\"; " +
            "fi; " +
            "exit 0";

        var hadOk = false;
        var hadSelfGrant = false;
        var hadFailure = false;
        string? principal = null;
        string? upn = null;
        string? rolesLine = null;
        int narrowCount = 0;
        string? grantErr = null;

        await RunInContainerAsync(sandboxImage, script, line =>
        {
            if (line.Contains("PREFLIGHT_RBAC: principal="))
                principal = line.Split('=', 2)[1].Trim();
            else if (line.Contains("PREFLIGHT_RBAC: upn="))
                upn = line.Split('=', 2)[1].Trim();
            else if (line.Contains("PREFLIGHT_RBAC: roles="))
                rolesLine = line.Split('=', 2)[1].Trim();
            else if (line.Contains("PREFLIGHT_RBAC: narrow_role_count="))
                int.TryParse(line.Split('=', 2)[1].Trim(), out narrowCount);
            else if (line.Contains("PREFLIGHT_RBAC: OK_HAS_RBAC_WRITE"))
                hadOk = true;
            else if (line.Contains("PREFLIGHT_RBAC: SELF_GRANT_OK"))
                hadSelfGrant = true;
            else if (line.Contains("PREFLIGHT_RBAC: SELF_GRANT_FAILED"))
                hadFailure = true;
            else if (line.Contains("PREFLIGHT_RBAC: grant_err="))
                grantErr = line.Split('=', 2)[1].Trim();
        }, ct);

        var who = string.IsNullOrEmpty(upn) ? principal ?? "(unknown)" : $"{upn} ({principal})";

        if (hadOk)
        {
            log("info",
                $"RBAC preflight OK: {who} has a role-assignment-capable role on " +
                $"subscription {subscriptionId} (roles: {rolesLine}). Templates " +
                "that create role assignments will validate.");
            return;
        }

        if (hadSelfGrant)
        {
            log("info",
                $"RBAC preflight: {who} lacked a role-assignment-capable role; " +
                $"auto-granted 'User Access Administrator' on subscription " +
                $"{subscriptionId}. Azure RBAC propagation takes 1-3 minutes: " +
                "if azd provision still reports AuthorizationFailed on this run, " +
                "wait ~2 min and retry the deploy.");
            return;
        }

        if (hadFailure)
        {
            // Tailor the message to what we actually observed.
            // Three sub-cases:
            //  (a) zero roles at sub scope AND zero narrow roles under
            //      the sub => identity is a complete outsider on this
            //      sub (guest invited to the directory but nothing
            //      assigned). Cannot deploy ANYTHING here.
            //  (b) zero roles at sub scope but >0 narrow roles under
            //      the sub => "guest with limited resource-level
            //      access" (the user's actual case: Cognitive Services
            //      User on Foundry). Cannot deploy a template that
            //      creates RGs / role assignments. azd provision will
            //      fail at validate because Microsoft.Resources/
            //      deployments/validate/action requires *Contributor*
            //      at sub scope, not just UAA.
            //  (c) some role at sub scope (e.g. Reader/Contributor) but
            //      not a UAA-capable one => user can deploy *most*
            //      stuff but not templates that create role
            //      assignments. Just needs UAA on top.
            var hasSubScopeRole = !string.IsNullOrWhiteSpace(rolesLine);
            string headline;
            string remediation;

            if (!hasSubScopeRole && narrowCount == 0)
            {
                headline =
                    $"RBAC preflight FAILED: {who} has NO role assignments " +
                    $"anywhere on subscription {subscriptionId}. You are signed in " +
                    "to the right tenant, but this subscription has not been " +
                    "shared with your identity. azd provision cannot deploy here.";
                remediation =
                    "Fix options (in order of preference):\n" +
                    "  1) [RECOMMENDED] Switch to a subscription where you ARE Owner " +
                    "(personal MSDN/Visual Studio sub, Azure for Students, your own " +
                    "pay-as-you-go) -- change Subscription ID in the Hub UI and retry.\n" +
                    "  2) Ask a subscription Owner of this sub to grant you BOTH:\n" +
                    $"       az role assignment create --assignee {principal} \\\n" +
                    "         --role 'Contributor' \\\n" +
                    $"         --scope /subscriptions/{subscriptionId}\n" +
                    $"       az role assignment create --assignee {principal} \\\n" +
                    "         --role 'User Access Administrator' \\\n" +
                    $"         --scope /subscriptions/{subscriptionId}\n" +
                    "     Both are required: Contributor for the deploy itself, UAA " +
                    "for the role assignments the template creates.";
            }
            else if (!hasSubScopeRole && narrowCount > 0)
            {
                headline =
                    $"RBAC preflight FAILED: {who} has {narrowCount} narrow role " +
                    $"assignment(s) on individual resources under subscription " +
                    $"{subscriptionId}, but ZERO roles at the subscription scope " +
                    "itself. This is the typical 'guest user invited to a single " +
                    "Foundry/Cognitive Services account' setup -- it lets you USE " +
                    "those resources but NOT deploy new ones. azd provision needs " +
                    "Microsoft.Resources/deployments/validate/action (Contributor " +
                    "at sub scope) which you don't have.";
                remediation =
                    "Fix options (in order of preference):\n" +
                    "  1) [RECOMMENDED] Switch to a subscription where you ARE Owner " +
                    "and retry -- in the Hub UI, change Subscription ID. Most azd " +
                    "templates need full sub-scope rights that a managed/shared sub " +
                    "almost never grants to guests.\n" +
                    "  2) Ask a sub Owner of this sub to grant you BOTH " +
                    "Contributor + User Access Administrator at sub scope:\n" +
                    $"       az role assignment create --assignee {principal} \\\n" +
                    "         --role 'Contributor' \\\n" +
                    $"         --scope /subscriptions/{subscriptionId}\n" +
                    $"       az role assignment create --assignee {principal} \\\n" +
                    "         --role 'User Access Administrator' \\\n" +
                    $"         --scope /subscriptions/{subscriptionId}";
            }
            else
            {
                // (c) has some role but not UAA/Owner.
                headline =
                    $"RBAC preflight FAILED: {who} has roles [{rolesLine}] at sub " +
                    $"scope on {subscriptionId}, but none of them grant " +
                    "Microsoft.Authorization/roleAssignments/write. The deploy can " +
                    "provision resources but will fail when the template creates " +
                    "role assignments (managed identity binding, etc.). Self-grant " +
                    "of UAA was rejected, so you are not Owner here.";
                remediation =
                    "Fix: ask a sub Owner to add User Access Administrator on top " +
                    "of your existing role:\n" +
                    $"   az role assignment create --assignee {principal} \\\n" +
                    "     --role 'User Access Administrator' \\\n" +
                    $"     --scope /subscriptions/{subscriptionId}\n" +
                    "Then retry the deploy (cached credentials still valid; no " +
                    "re-login needed).";
            }

            var msg = headline + "\n" + remediation;
            if (!string.IsNullOrWhiteSpace(grantErr))
                msg += $"\nSelf-grant attempt error (truncated): {grantErr}";
            log("warn", msg);
            return;
        }

        log("warn",
            "RBAC preflight: probe was inconclusive (could not enumerate roles or " +
            "resolve principal). Continuing -- if azd provision later fails with " +
            "AuthorizationFailed, the orchestrator will surface a remediation " +
            "message at that point.");
    }

    /// <summary>
    /// Docker CLI args that mount the named volume as /root/.azure so both
    /// subsequent steps of the same deploy AND future deploys see the same
    /// authenticated profile.
    /// </summary>
    public static IEnumerable<string> VolumeMountArgs()
    {
        yield return "-v";
        yield return $"{VolumeName}:/root/.azure";
        yield return "-e";
        yield return "AZURE_CONFIG_DIR=/root/.azure";
        // Persistent docker CLI config: see DockerConfigVolumeName XML doc.
        // DOCKER_CONFIG points the docker CLI at our volume (default would
        // be the user's $HOME/.docker which is fine here too, but being
        // explicit prevents surprises if a step changes HOME).
        yield return "-v";
        yield return $"{DockerConfigVolumeName}:/root/.docker";
        yield return "-e";
        yield return "DOCKER_CONFIG=/root/.docker";
    }

    private static async Task EnsureVolumeExistsAsync(string volumeName, CancellationToken ct)
    {
        var VolumeName = volumeName; // keep the rest of the method readable
        // Why this is so defensive: on Docker Desktop (Windows / macOS)
        // the host daemon socket is a Unix socket FORWARDED over a Windows
        // named pipe or a lightweight VM proxy. Short-lived commands like
        // 'docker volume create' that write a trailing newline to stdout
        // can race with the forwarded pipe's close sequence and get
        // SIGPIPE (exit 141), even when the .NET side sets an explicit
        // PipeTarget. We saw this reliably on arm64 Docker Desktop.
        //
        // The reliable workaround is to have a REAL shell redirect stdout
        // and stderr to /dev/null before docker ever writes: with
        //   sh -c 'docker volume create X >/dev/null 2>&1'
        // docker's stdout is connected to the /dev/null device file (not
        // a pipe we own), so there is nothing that can be closed out from
        // under it.
        //
        // As a final safety net, if volume creation still fails we log a
        // warning and return WITHOUT throwing: the volume is an
        // optimisation (caches the sandbox's 'az login' across deploys),
        // not a correctness requirement. Without it the first sandbox
        // step will ask for a device code once per deploy instead of
        // reusing a cached token. Annoying but recoverable � preferable
        // to aborting the whole pipeline.

        // Step 1: already present? (cheap, cached after first run)
        var inspect = await Cli.Wrap("sh")
            .WithArguments(new[] { "-c", $"docker volume inspect {VolumeName} >/dev/null 2>&1" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (inspect.ExitCode == 0) return;

        // Step 2: create, SIGPIPE-proof via shell redirect.
        async Task<int> TryCreate()
        {
            var r = await Cli.Wrap("sh")
                .WithArguments(new[] { "-c", $"docker volume create {VolumeName} >/dev/null 2>&1" })
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(ct);
            return r.ExitCode;
        }

        var exit = await TryCreate();
        if (exit != 0)
        {
            await Task.Delay(500, ct);
            exit = await TryCreate();
        }

        if (exit == 0) return;

        // Maybe another process created it concurrently � verify.
        var reinspect = await Cli.Wrap("sh")
            .WithArguments(new[] { "-c", $"docker volume inspect {VolumeName} >/dev/null 2>&1" })
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);
        if (reinspect.ExitCode == 0) return;

        // Graceful degradation: log + continue. The sandbox deploy will
        // still work, it just won't cache Azure credentials between runs.
        // The caller bubbles logs up to the UI via the onLine callback
        // passed to EnsureAsync; we cannot access it here, so we write
        // to Console � the containerised app captures it in docker logs.
        Console.Error.WriteLine(
            $"[SandboxAzureAuth] WARN: could not create docker volume '{VolumeName}' " +
            $"(exit {exit}). Continuing without cached Azure auth � each deploy " +
            "may require a fresh device-code login. " +
            "If this persists, restart Docker Desktop and try again.");
    }

    private static async Task<bool> RunInContainerAsync(
        string image, string shellCommand, Action<string> onLine, CancellationToken ct)
    {
        var args = new List<string>
        {
            "run", "--rm", "-i",
            "--dns", "1.1.1.1",
            "--dns", "8.8.8.8",
            "--memory", "4g",
            "--memory-swap", "6g"
        };
        args.AddRange(VolumeMountArgs());
        args.Add(image);
        args.Add("bash");
        args.Add("-lc");
        args.Add(shellCommand);

        int exit = -1;
        await foreach (var ev in Cli.Wrap("docker")
            .WithArguments(args)
            .WithValidation(CommandResultValidation.None)
            .ListenAsync(ct))
        {
            switch (ev)
            {
                case StandardOutputCommandEvent o: onLine(o.Text); break;
                case StandardErrorCommandEvent e:  onLine(e.Text); break;
                case ExitedCommandEvent x:         exit = x.ExitCode; break;
            }
        }
        return exit == 0;
    }
}
