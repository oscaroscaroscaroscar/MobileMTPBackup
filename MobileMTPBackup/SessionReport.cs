using System.Text.Json;

namespace MobileMTPBackup;

public sealed record BackupSessionReport(
    string Version,
    string Device,
    string Source,
    string Destination,
    DateTime StartedAt,
    DateTime FinishedAt,
    int FilesCopied,
    int FilesSkipped,
    int FilesFailed,
    long BytesCopied,
    bool Cancelled,
    string? Error);

public static class SessionReportWriter
{
    public static async Task<string> WriteAsync(string destinationRoot,BackupSessionReport report)
    {
        Directory.CreateDirectory(destinationRoot);
        string reports=Path.Combine(destinationRoot,"reports");
        Directory.CreateDirectory(reports);
        string name=$"backup-session-{report.FinishedAt:yyyyMMdd-HHmmss}.json";
        string path=Path.Combine(reports,name);
        var options=new JsonSerializerOptions{WriteIndented=true};
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,options));
        return path;
    }
}
