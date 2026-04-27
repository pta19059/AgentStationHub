using System.Diagnostics;
using LibGit2Sharp;

namespace AgentStationHub.Services.Tools;

public static class GitTool
{
    public static Task CloneAsync(string repoUrl, string workDir, Action<string>? onProgress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var opts = new CloneOptions();
            var sw = Stopwatch.StartNew();
            long lastReportedMs = -1;
            int lastReportedObjects = -1;
            bool finalReported = false;

            opts.FetchOptions.OnTransferProgress = p =>
            {
                if (ct.IsCancellationRequested) return false;

                // Throttle: emit progress at most every ~500ms, plus always emit
                // the final update exactly once when all objects have been received.
                var elapsed = sw.ElapsedMilliseconds;
                var isFinal = p.TotalObjects > 0 && p.ReceivedObjects >= p.TotalObjects;
                if (isFinal && finalReported) return true;

                if (isFinal || lastReportedMs < 0 || elapsed - lastReportedMs >= 500)
                {
                    if (isFinal || p.ReceivedObjects != lastReportedObjects)
                    {
                        onProgress?.Invoke($"receiving {p.ReceivedObjects}/{p.TotalObjects} ({p.ReceivedBytes / 1024} KB)");
                        lastReportedMs = elapsed;
                        lastReportedObjects = p.ReceivedObjects;
                        if (isFinal) finalReported = true;
                    }
                }
                return true;
            };

            long lastCheckoutMs = -1;
            opts.OnCheckoutProgress = (path, completed, total) =>
            {
                if (total == 0) return;
                var elapsed = sw.ElapsedMilliseconds;
                var isFinal = completed >= total;
                if (isFinal || lastCheckoutMs < 0 || elapsed - lastCheckoutMs >= 500)
                {
                    onProgress?.Invoke($"checkout {completed}/{total}");
                    lastCheckoutMs = elapsed;
                }
            };

            Repository.Clone(repoUrl, workDir, opts);

            // LibGit2Sharp (like git-for-windows with core.autocrlf=true) may
            // have converted LF -> CRLF on checkout. When the workspace is
            // later bind-mounted into a Linux container, shell scripts whose
            // shebang line ends with '\r' produce the cryptic kernel error
            // 'cannot execute: required file not found' because the kernel
            // tries to exec the literal interpreter path '/bin/bash\r'.
            // Normalise shell scripts back to LF endings after clone so
            // 'azd' hooks and similar scripts run correctly.
            NormalizeShellScripts(workDir, onProgress);
        }, ct);

    private static void NormalizeShellScripts(string workDir, Action<string>? onProgress)
    {
        try
        {
            var opts = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var scripts = Directory.EnumerateFiles(workDir, "*.sh", opts)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
                .ToList();

            int fixedCount = 0;
            foreach (var path in scripts)
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (!ContainsCrlf(bytes)) continue;
                    File.WriteAllBytes(path, StripCr(bytes));
                    fixedCount++;
                }
                catch { /* best-effort per file */ }
            }

            if (fixedCount > 0)
                onProgress?.Invoke($"normalised {fixedCount} .sh script(s) to LF line endings");
        }
        catch { /* best-effort overall */ }
    }

    private static bool ContainsCrlf(byte[] b)
    {
        for (int i = 0; i < b.Length - 1; i++)
            if (b[i] == 0x0D && b[i + 1] == 0x0A) return true;
        return false;
    }

    private static byte[] StripCr(byte[] b)
    {
        var result = new byte[b.Length];
        int o = 0;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] == 0x0D && i + 1 < b.Length && b[i + 1] == 0x0A) continue;
            result[o++] = b[i];
        }
        Array.Resize(ref result, o);
        return result;
    }
}
