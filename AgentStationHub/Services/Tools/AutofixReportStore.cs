using System.Text.Json;
using System.Text.Json.Serialization;
using AgentStationHub.Models;

namespace AgentStationHub.Services.Tools;

/// <summary>
/// Persists <see cref="AutofixReport"/> instances as JSON files in a
/// session-scoped directory. Reports survive container restarts and
/// can be downloaded from the UI.
/// </summary>
public static class AutofixReportStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Directory where autofix reports are stored.
    /// </summary>
    public static string ReportsDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "autofix-reports");

    /// <summary>
    /// Saves the report to disk and returns the file path.
    /// </summary>
    public static async Task<string> SaveAsync(AutofixReport report)
    {
        Directory.CreateDirectory(ReportsDir);
        var fileName = $"{report.SessionId}_{report.StartedAt:yyyyMMdd-HHmmss}.json";
        var filePath = Path.Combine(ReportsDir, fileName);

        var json = JsonSerializer.Serialize(report, JsonOpts);
        await File.WriteAllTextAsync(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Lists all saved reports, most recent first.
    /// </summary>
    public static IReadOnlyList<string> ListReports()
    {
        if (!Directory.Exists(ReportsDir)) return Array.Empty<string>();
        return Directory.GetFiles(ReportsDir, "*.json")
            .OrderByDescending(f => f)
            .ToList();
    }
}
