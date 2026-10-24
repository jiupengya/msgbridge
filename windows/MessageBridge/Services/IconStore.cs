using System.IO;
using System.Security.Cryptography;

namespace MessageBridge.Services;

/// <summary>
/// 通知图标落盘。
/// 手机传来的图标是 base64，直接塞进 messages.json 会让文件膨胀到几百 KB，
/// 而且每条新消息都要全量重写一次。这里只在收到时解码一次、按内容哈希存成 png，
/// JSON 里只保留文件名，列表和 Toast 都直接读文件。
/// </summary>
public sealed class IconStore
{
    private readonly string _dir;
    private readonly object _lock = new();

    public IconStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "icons");
        Directory.CreateDirectory(_dir);
    }

    /// <summary>保存图标并返回文件名（同内容只存一份）；空内容或解码失败返回 null。</summary>
    public string? Save(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var name = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant() + ".png";
            var path = Path.Combine(_dir, name);
            lock (_lock)
            {
                if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            }
            return name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>图标文件名 -> 绝对路径；文件名为空时返回空串。</summary>
    public string PathOf(string? name)
        => string.IsNullOrEmpty(name) ? "" : Path.Combine(_dir, name);

    /// <summary>删除没有被任何消息引用的图标文件。</summary>
    public void Trim(IEnumerable<string> keepNames)
    {
        try
        {
            var keep = new HashSet<string>(
                keepNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
            foreach (var f in new DirectoryInfo(_dir).GetFiles("*.png"))
            {
                if (keep.Contains(f.Name)) continue;
                try { f.Delete(); } catch { }
            }
        }
        catch
        {
            // 清理失败不影响收发
        }
    }
}
