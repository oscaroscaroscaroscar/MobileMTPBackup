using System.IO;
using System.Text.Json;

namespace MobileMTPBackup;

public sealed record AppSettings(string DestinationPath, string? LastDeviceName, string ConnectionMode = "USB/MTP", string WifiHost = "", int WifiPort = 8765)
{
    public static AppSettings Default => new(@"C:\MobilBackup", null, "USB/MTP", "", 8765);
}

public static class AppSettingsStore
{
    private static string SettingsDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MobileMTPBackup");

    private static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return AppSettings.Default;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (settings is null || string.IsNullOrWhiteSpace(settings.DestinationPath)) return AppSettings.Default;
            return settings;
        }
        catch
        {
            return AppSettings.Default;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, SettingsPath, true);
    }
}
