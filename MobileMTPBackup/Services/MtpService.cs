using System.IO;
using System.Reflection;
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
        var asm = typeof(MediaDevice).Assembly;
        object? raw = null;
        foreach (var type in asm.GetTypes())
        {
            var method = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetDevices" && m.GetParameters().Length == 0);
            if (method is null) continue;
            raw = method.Invoke(null, null);
            if (raw is not null) break;
        }
        if (raw is not System.Collections.IEnumerable enumerable) return Array.Empty<MtpDeviceInfo>();
        var result = new List<MtpDeviceInfo>();
        foreach (var item in enumerable)
        {
            if (item is not MediaDevice d) continue;
            result.Add(new MtpDeviceInfo(d.DeviceId, string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Description : d.FriendlyName, d));
        }
        return result;
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
                result.Add(new(p, i.Name, true, null, i.CreationTime, i.LastWriteTime));
            }
            foreach (var p in device.NativeDevice.GetFiles(path))
            {
                var i = device.NativeDevice.GetFileInfo(p);
                long? length = i.Length > long.MaxValue ? long.MaxValue : (long)i.Length;
                result.Add(new(p, i.Name, false, length, i.CreationTime, i.LastWriteTime));
            }
            return result.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name).ToList();
        }
        finally { device.NativeDevice.Disconnect(); }
    }

    public void DownloadFile(MtpDeviceInfo device, string remotePath, string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        device.NativeDevice.Connect();
        try { device.NativeDevice.DownloadFile(remotePath, localPath); }
        finally { device.NativeDevice.Disconnect(); }
    }
}
