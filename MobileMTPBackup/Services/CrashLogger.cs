namespace MobileMTPBackup.Services;

public static class CrashLogger
{
    public static string Log(Exception ex)
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MobileMTPBackup", "Logs");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        File.WriteAllText(path, ex.ToString());
        return path;
    }
}
