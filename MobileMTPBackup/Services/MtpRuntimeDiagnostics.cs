using System.Reflection;
using System.Text;

namespace MobileMTPBackup.Services;

public static class MtpRuntimeDiagnostics
{
    public static string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"OS: {Environment.OSVersion}");
        sb.AppendLine($".NET: {Environment.Version}");
        sb.AppendLine($"64-bit process: {Environment.Is64BitProcess}");
        var type = Type.GetType("MediaDevices.MediaDevice, MediaDevices", throwOnError: false);
        sb.AppendLine($"MediaDevices loaded: {type is not null}");
        if (type is not null)
        {
            foreach (var name in new[] { "GetDevices", "Connect", "Disconnect", "DownloadFile", "GetDirectoryInfo", "GetFileInfo" })
                sb.AppendLine($"{name}: {type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance).Count(m => m.Name == name)} overload(s)");
        }
        return sb.ToString();
    }
}
