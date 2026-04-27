using CliWrap;
using CliWrap.EventStream;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// One-shot preflight that runs inside an alpine helper container after
/// the per-session workspace volume has been staged. Walks every
/// <c>docker-compose*.y?ml</c> file in <c>/workspace</c>, extracts each
/// <c>env_file:</c> reference (both the scalar form
/// <c>env_file: ./foo.env</c> and the YAML list form
/// <c>env_file:\n  - ./foo.env</c>), resolves it to an absolute path
/// relative to the compose file's directory, and <c>touch</c>es any that
/// don't already exist (creating parent directories as needed).
///
/// Why: many Azure samples ship a postprovision hook that runs
/// <c>docker compose build</c>. Compose v2 refuses to build if a service
/// declares an <c>env_file</c> that doesn't exist on disk, exiting
/// <c>code 14</c> with <c>env file ... not found</c>. The hook scripts
/// are SUPPOSED to seed these files (e.g. <c>cp .env.sample .env</c>) but
/// frequently <c>skip</c> services with no <c>.env.sample</c>, leaving
/// compose with a dangling reference. The Doctor used to chase this
/// failure across the MCP servers one-by-one and routinely exhausted
/// its 8-attempt budget on samples like azure-ai-travel-agents. Touching
/// the files preemptively (creating empty placeholders) makes compose
/// accept them; any variable the service genuinely needs at runtime is
/// still surfaced through the regular <c>environment:</c> block or the
/// parent shell, so the empty file is never the wrong answer for build-
/// time validation.
///
/// The helper is best-effort and idempotent: it never overwrites an
/// existing file, never modifies the compose YAML, and never fails the
/// deploy on its own � if the parser misses a reference the original
/// failure simply resurfaces and the Doctor takes over.
/// </summary>
public static class WorkspaceEnvFilePrimer
{
    // Python script kept as a separate string so the awkward quoting
    // (Python literals inside a C# verbatim string) is contained. The
    // script avoids double-quote characters entirely so the C# verbatim
    // block never needs the """" escape � single quotes work everywhere
    // in Python where we'd otherwise want doubles.
    private const string PrimerScript = @"
import os, re, glob

ROOT = '/workspace'
ENV_FILE_RE = re.compile(r'^(\s*)env_file\s*:\s*(.*)$')
LIST_ITEM_RE = re.compile(r'^(\s*)-\s*(.+?)\s*(?:#.*)?$')
created, scanned = 0, 0

def clean_value(v):
    v = v.strip()
    if '#' in v:
        v = v.split('#', 1)[0].strip()
    # Strip surrounding single or double quotes if balanced.
    if len(v) >= 2 and v[0] == v[-1] and v[0] in (chr(34), chr(39)):
        v = v[1:-1]
    return v.strip()

for path in glob.glob(os.path.join(ROOT, '**', 'docker-compose*.y*ml'), recursive=True):
    if '/node_modules/' in path or '/.git/' in path:
        continue
    scanned += 1
    base = os.path.dirname(path)
    try:
        with open(path, 'r', encoding='utf-8', errors='replace') as f:
            lines = f.readlines()
    except Exception as ex:
        print('! could not read ' + path + ': ' + str(ex))
        continue
    refs = []
    i = 0
    while i < len(lines):
        line = lines[i].rstrip('\n')
        m = ENV_FILE_RE.match(line)
        if m:
            indent = m.group(1)
            rest = m.group(2)
            if rest.strip() and not rest.lstrip().startswith('-') and not rest.lstrip().startswith('|'):
                v = clean_value(rest)
                if v:
                    refs.append(v)
            else:
                j = i + 1
                while j < len(lines):
                    item = lines[j].rstrip('\n')
                    if not item.strip():
                        j += 1; continue
                    li = LIST_ITEM_RE.match(item)
                    if li and len(li.group(1)) > len(indent):
                        v = clean_value(li.group(2))
                        if v:
                            refs.append(v)
                        j += 1
                    else:
                        break
                i = j - 1
        i += 1

    for ref in refs:
        if not ref or '${' in ref or ref.startswith('http'):
            continue
        target = ref if os.path.isabs(ref) else os.path.normpath(os.path.join(base, ref))
        if os.path.exists(target):
            continue
        try:
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, 'a'):
                os.utime(target, None)
            print('+ touched ' + target + ' (from ' + os.path.relpath(path, ROOT) + ')')
            created += 1
        except Exception as ex:
            print('! could not touch ' + target + ': ' + str(ex))

print('env-file primer: scanned ' + str(scanned) + ' compose file(s), created ' + str(created) + ' placeholder(s)')
";

    public static async Task PrimeAsync(
        string workspaceVolume,
        Action<string, string> log,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "run", "--rm", "-i",
            "-v", $"{workspaceVolume}:/workspace",
            "python:3.12-alpine",
            "python", "-c", PrimerScript
        };

        try
        {
            log("status", "Priming missing compose env_file placeholders in /workspace...");
            int exit = -1;
            await foreach (var ev in Cli.Wrap("docker")
                .WithArguments(args)
                .WithValidation(CommandResultValidation.None)
                .ListenAsync(ct))
            {
                switch (ev)
                {
                    case StandardOutputCommandEvent o:
                        if (!string.IsNullOrWhiteSpace(o.Text)) log("info", o.Text);
                        break;
                    case StandardErrorCommandEvent e:
                        if (!string.IsNullOrWhiteSpace(e.Text)) log("warn", e.Text);
                        break;
                    case ExitedCommandEvent x:
                        exit = x.ExitCode; break;
                }
            }
            if (exit != 0)
            {
                log("warn", $"env-file primer exited {exit}; continuing - the deploy will surface the original error if any reference is still missing.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log("warn", $"env-file primer threw: {ex.GetType().Name}: {ex.Message}. Continuing without primer.");
        }
    }
}
