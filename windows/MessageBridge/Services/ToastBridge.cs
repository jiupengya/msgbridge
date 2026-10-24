using System.IO;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace MessageBridge.Services;

/// <summary>
/// Windows 系统通知（Toast）。
/// 使用 AUMID 身份（见 <see cref="AumidShortcut"/>）调用 CreateToastNotifier，
/// ToastGeneric 模板 + appLogoOverride 方形图标，显示通知来源的应用图标而非系统感叹号。
///
/// 关键点：Windows 对同一应用的横幅是「排队」显示的（每条约 5 秒），
/// 连续多条时后发通知会等很久。这里统一使用固定 Tag + Group，
/// 并在 Show 前先移除上一条，让新通知立即顶替上一条，不再排队。
/// 失败时写入 logs/toast.log，便于排查（系统关闭通知、AUMID 未注册等）。
/// </summary>
public sealed class ToastBridge
{
    // Tag/Group 都限制在 16 字符内；固定值保证同一应用的通知互相替换
    private const string Tag = "msgbridge";
    private const string Group = "message";

    private readonly string _logPath;
    private readonly object _logLock = new();
    private readonly Lazy<ToastNotifier> _notifier;

    public ToastBridge(string dataDir)
    {
        _logPath = Path.Combine(dataDir, "logs", "toast.log");
        _notifier = new Lazy<ToastNotifier>(() =>
            ToastNotificationManager.CreateToastNotifier(AumidShortcut.AppId));
    }

    /// <summary>弹出系统通知；返回是否成功提交给系统。</summary>
    /// <param name="iconPath">图标 png 的绝对路径，null 表示不带图标。</param>
    /// <param name="silent">静默通知：不响铃、不弹预览横幅。</param>
    public bool Show(string title, string content, string? iconPath, bool silent = false)
    {
        try
        {
            var xml = "<toast><visual><binding template=\"ToastGeneric\">";
            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
            {
                // 不加 hint-crop，图标保持方形显示
                xml += "<image placement=\"appLogoOverride\" src=\"" +
                       new Uri(iconPath).AbsoluteUri +
                       "\"/>";
            }
            xml += "<text>" + Escape(string.IsNullOrEmpty(title) ? "Message" : title) + "</text>";
            xml += "<text>" + Escape(content) + "</text>";
            xml += "</binding></visual>";
            if (silent)
            {
                // 手机端标记为静默的通知，电脑端也不响
                xml += "<audio silent=\"true\"/>";
            }
            xml += "</toast>";

            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var toast = new ToastNotification(doc)
            {
                Tag = Tag,
                Group = Group
            };

            // 先清掉上一条同 Tag 通知，避免系统排队导致后面的通知等好几秒
            try
            {
                ToastNotificationManager.History.Remove(Tag, Group, AumidShortcut.AppId);
            }
            catch
            {
                // 没有旧通知时移除会失败，忽略
            }

            _notifier.Value.Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Show failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("\r\n", "\n")
            .Replace("\n", "&#10;");
    }

    private void Log(string message)
    {
        try
        {
            lock (_logLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
