namespace AgentStationHub.Services.Tools;

public sealed class FileTool
{
    private readonly string _root;
    public FileTool(string root) => _root = Path.GetFullPath(root);

    public string? ReadText(string relativePath, int maxBytes = 20_000)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return null;
        using var fs = File.OpenRead(full);
        var len = (int)Math.Min(maxBytes, fs.Length);
        var buf = new byte[len];
        _ = fs.Read(buf, 0, len);
        return System.Text.Encoding.UTF8.GetString(buf);
    }

    public IEnumerable<string> ListFiles(string relativeDir, string searchPattern = "*", int maxItems = 200)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativeDir));
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(full))
            return [];
        return Directory.EnumerateFiles(full, searchPattern, SearchOption.AllDirectories)
                        .Take(maxItems)
                        .Select(p => Path.GetRelativePath(_root, p));
    }
}
