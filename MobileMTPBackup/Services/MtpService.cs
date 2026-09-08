using System.IO;
using MediaDevices;

namespace MobileMTPBackup;

public sealed record MtpDeviceInfo(string Id, string Name, MediaDevice NativeDevice)
{
    public override string ToString() => Name;
}

public sealed record MtpEntry(string FullName,string Name,bool IsDirectory,long? Length,DateTime? DateCreated,DateTime? DateModified);

public sealed class MtpService
{
    public IReadOnlyList<MtpDeviceInfo> GetDevices()
    {
        var devices = MediaDevice.GetDevices().ToList();
        return devices.Select(d => new MtpDeviceInfo(
            d.DeviceId ?? "",
            !string.IsNullOrWhiteSpace(d.FriendlyName) ? d.FriendlyName : (!string.IsNullOrWhiteSpace(d.Description) ? d.Description : "MTP USB Device"),
            d)).ToList();
    }

    public string GetDiagnosticSummary()
    {
        var asm = typeof(MediaDevice).Assembly.GetName();
        var devices = MediaDevice.GetDevices().ToList();
        return $"MediaDevices {asm.Version}; hittade {devices.Count} enhet(er).";
    }

    public IReadOnlyList<MtpEntry> GetRootEntries(MtpDeviceInfo device) => GetEntries(device, "\\");

    public IReadOnlyList<MtpEntry> GetEntries(MtpDeviceInfo device, string path)
    {
        device.NativeDevice.Connect();
        try
        {
            var result = new List<MtpEntry>();
            foreach (var p in device.NativeDevice.GetDirectories(path))
            {
                var i = device.NativeDevice.GetDirectoryInfo(p);
                result.Add(new MtpEntry(p, i.Name, true, null, ValidDate(i.CreationTime), ValidDate(i.LastWriteTime)));
            }
            foreach (var p in device.NativeDevice.GetFiles(path))
            {
                var i = device.NativeDevice.GetFileInfo(p);
                result.Add(new MtpEntry(p, i.Name, false, Convert.ToInt64(i.Length), ValidDate(i.CreationTime), ValidDate(i.LastWriteTime)));
            }
            return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name).ToList();
        }
        finally
        {
            if (device.NativeDevice.IsConnected) device.NativeDevice.Disconnect();
        }
    }

    public void DownloadFile(MtpDeviceInfo device, string remotePath, string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        device.NativeDevice.Connect();
        try
        {
            using var output = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None);
            device.NativeDevice.DownloadFile(remotePath, output);
            output.Flush(true);
        }
        finally
        {
            if (device.NativeDevice.IsConnected) device.NativeDevice.Disconnect();
        }
    }

    private static DateTime? ValidDate(DateTime value)
        => value.Year >= 1970 && value <= DateTime.Now.AddDays(2) ? value : null;
}
