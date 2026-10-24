using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace MessageBridge.Core;

/// <summary>
/// 已配对设备清单（持久化到 %LocalAppData%\MessageBridge\trust.json）。
/// 配对成功的设备，下次连接直接放行（参考 KDE Connect 的已信任设备）。
/// </summary>
public sealed class TrustStore
{
    public sealed class TrustedDevice
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public DateTime LastSeen { get; set; } = DateTime.Now;
    }

    private readonly string _path;
    private readonly ConcurrentDictionary<string, TrustedDevice> _devices = new();

    public TrustStore(string dataDir)
    {
        _path = Path.Combine(dataDir, "trust.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(_path));
            if (list == null) return;
            foreach (var d in list)
            {
                if (!string.IsNullOrEmpty(d.DeviceId))
                    _devices[d.DeviceId] = d;
            }
        }
        catch
        {
            // 损坏的信任文件忽略，视为无信任设备
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_devices.Values.ToList(),
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
        catch
        {
            // 写失败不影响内存运行
        }
    }

    public bool IsTrusted(string deviceId) =>
        !string.IsNullOrEmpty(deviceId) && _devices.ContainsKey(deviceId);

    public void Add(string deviceId, string deviceName)
    {
        _devices[deviceId] = new TrustedDevice
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            LastSeen = DateTime.Now
        };
        Save();
    }

    public void Remove(string deviceId)
    {
        _devices.TryRemove(deviceId, out _);
        Save();
    }

    public IReadOnlyCollection<TrustedDevice> All() => _devices.Values.ToList();
}
