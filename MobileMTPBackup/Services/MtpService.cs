using MediaDevices;

namespace MobileMTPBackup;

public sealed record MtpDeviceInfo(string Id, string Name, MediaDevice NativeDevice)
{
    public override string ToString() => Name;
}

public sealed record MtpEntry(string FullName,string Name,bool IsDirectory,long? Length,DateTime? DateCreated,DateTime? DateModified);

public sealed class MtpService
{
    public IReadOnlyList<MtpDeviceInfo> GetDevices() => MediaDevice.GetDevices().Select(d => new MtpDeviceInfo(d.DeviceId, string.IsNullOrWhiteSpace(d.FriendlyName) ? d.Description : d.FriendlyName, d)).ToList();

    public IReadOnlyList<MtpEntry> GetRootEntries(MtpDeviceInfo device) => GetEntries(device, "\\");

    public IReadOnlyList<MtpEntry> GetEntries(MtpDeviceInfo device, string path)
    {
        device.NativeDevice.Connect();
        try
        {
            var result = new List<MtpEntry>();
            foreach (var p in device.NativeDevice.GetDirectories(path))
            {
                var i=device.NativeDevice.GetDirectoryInfo(p);
                result.Add(new(p,i.Name,true,null,i.CreationTime,i.LastWriteTime));
            }
            foreach (var p in device.NativeDevice.GetFiles(path))
            {
                var i=device.NativeDevice.GetFileInfo(p);
                result.Add(new(p,i.Name,false,i.Length,i.CreationTime,i.LastWriteTime));
            }
            return result.OrderByDescending(x=>x.IsDirectory).ThenBy(x=>x.Name).ToList();
        }
        finally { device.NativeDevice.Disconnect(); }
    }

    public void DownloadFile(MtpDeviceInfo device,string remotePath,string localPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        device.NativeDevice.Connect();
        try { device.NativeDevice.DownloadFile(remotePath,localPath); }
        finally { device.NativeDevice.Disconnect(); }
    }
}
