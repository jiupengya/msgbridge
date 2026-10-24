using System.IO;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

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

    /// <summary>
    /// 从 png 文件解码出位图。
    /// WinUI 的 BitmapImage 走 URI 异步解码，这里只负责建对象（必须在 UI 线程调用），
    /// 实际解码由 XAML 在渲染时完成，列表滚动不会重复解码同一个对象。
    /// 必须用 file:/// 开头的绝对 URI，直接传 "C:\a\b.png" 这种路径 WinUI 不认。
    /// </summary>
    public static ImageSource? LoadBitmap(string? path, int decodeWidth = 64)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var uri = new Uri("file:///" + path.Replace('\\', '/'));
            return new BitmapImage { UriSource = uri };
        }
        catch
        {
            return null;
        }
    }

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
