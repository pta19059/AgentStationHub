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
        var script =
            "set +e; " +
            "OBJ=$(az ad signed-in-user show --query id -o tsv 2>/dev/null); " +
            "if [ -z \"$OBJ\" ]; then " +
            "  OBJ=$(az account show --query user.name -o tsv 2>/dev/null); " +
            "fi; " +
            "if [ -z \"$OBJ\" ]; then " +
            "  echo 'PREFLIGHT_RBAC: could not resolve current principal'; exit 0; " +
            "fi; " +
            "echo \"PREFLIGHT_RBAC: principal=$OBJ\"; " +
            $"ROLES=$(az role assignment list --assignee \"$OBJ\" " +
            $"  --scope /subscriptions/{subscriptionId} " +
            "  --include-inherited --include-groups " +
            "  --query \"[].roleDefinitionName\" -o tsv 2>/dev/null); " +
            "echo \"PREFLIGHT_RBAC: roles=$(echo $ROLES | tr '\\n' ',' | sed 's/,$//')\"; " +
            "if echo \"$ROLES\" | grep -qiE '^(Owner|User Access Administrator|Role Based Access Control Administrator)$'; then " +
            "  echo 'PREFLIGHT_RBAC: OK_HAS_RBAC_WRITE'; exit 0; " +
            "fi; " +
            "echo 'PREFLIGHT_RBAC: missing roleAssignments/write capable role; attempting self-grant'; " +
            $"az role assignment create --assignee \"$OBJ\" " +
            "  --role 'User Access Administrator' " +
            $"  --scope /subscriptions/{subscriptionId} " +
            "  --only-show-errors 2>&1 | tail -5; " +
            "if [ $? -eq 0 ]; then echo 'PREFLIGHT_RBAC: SELF_GRANT_OK'; else echo 'PREFLIGHT_RBAC: SELF_GRANT_FAILED'; fi; " +
            "exit 0";

        var hadOk = false;
        var hadSelfGrant = false;
        var hadFailure = false;
        string? principal = null;
        string? rolesLine = null;

        await RunInContainerAsync(sandboxImage, script, line =>
        {
            if (line.Contains("PREFLIGHT_RBAC: principal="))
                principal = line.Split('=', 2)[1].Trim();
            else if (line.Contains("PREFLIGHT_RBAC: roles="))
                rolesLine = line.Split('=', 2)[1].Trim();
            else if (line.Contains("PREFLIGHT_RBAC: OK_HAS_RBAC_WRITE"))
                hadOk = true;
            else if (line.Contains("PREFLIGHT_RBAC: SELF_GRANT_OK"))
                hadSelfGrant = true;
            else if (line.Contains("PREFLIGHT_RBAC: SELF_GRANT_FAILED"))
                hadFailure = true;
        }, ct);

        if (hadOk)
        {
            log("info",
                $"RBAC preflight: identity '{principal}' has a role-assignment-capable " +
                $"role on subscription {subscriptionId} (roles: {rolesLine ?? "n/a"}). " +
                "Templates that create role assignments will validate.");
            return;
        }

        if (hadSelfGrant)
        {
            log("info",
                $"RBAC preflight: identity '{principal}' lacked a role-assignment-capable " +
                $"role; auto-granted 'User Access Administrator' on /subscriptions/" +
                $"{subscriptionId}. NOTE: Azure RBAC propagation can take 1-3 minutes; " +
                "if azd provision still reports AuthorizationFailed on this run, just " +
                "retry the deploy in ~2 min and the new role will be visible.");
            return;
        }

        if (hadFailure)
        {
            log("warn",
                $"RBAC preflight: identity '{principal ?? "(unknown)"}' lacks " +
                "'Owner' / 'User Access Administrator' / 'Role Based Access " +
                $"Control Administrator' on subscription {subscriptionId}, AND " +
                "self-grant failed (which means you are not even the sub Owner). " +
                "Templates that create role assignments via Bicep will fail. " +
                "Ask a subscription Owner to run:" +
                $"\n  az role assignment create --assignee {principal ?? "<your-objectId>"}" +
                $"\n    --role 'User Access Administrator'" +
                $"\n    --scope /subscriptions/{subscriptionId}" +
                "\nThen retry the deploy. Cached credentials are unaffected; no re-login required.");
            return;
        }

        log("warn",
            "RBAC preflight: probe was inconclusive (could not enumerate roles). " +
            "Continuing � if azd provision later fails with AuthorizationFailed, " +
            "the orchestrator will surface a clear remediation message.");
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
