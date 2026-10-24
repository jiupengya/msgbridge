using System.IO;
using System.Windows.Forms;

namespace MessageBridge.Services;

/// <summary>
/// 托盘常驻 + 系统通知气泡（BalloonTip）+ 快捷菜单。
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _startupItem;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    /// <summary>点击「暂停推送 / 恢复推送」。</summary>
    public event Action? PauseToggleRequested;
    /// <summary>点击「开机自启」。</summary>
    public event Action? StartupToggleRequested;

    public Tray(Stream iconStream)
    {
        _icon = new NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconStream),
            Text = "Message · 手机消息推送",
            Visible = true
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 Message", null, (_, _) => OpenRequested?.Invoke());

        _pauseItem = new ToolStripMenuItem("暂停推送 30 分钟", null,
            (_, _) => PauseToggleRequested?.Invoke());
        menu.Items.Add(_pauseItem);

        _startupItem = new ToolStripMenuItem("开机自启", null,
            (_, _) => StartupToggleRequested?.Invoke());
        menu.Items.Add(_startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();
    }

    /// <summary>更新「暂停推送」菜单项的文字与勾选状态。</summary>
    public void SetPauseState(bool paused, string text)
    {
        _pauseItem.Text = text;
        _pauseItem.Checked = paused;
    }

    /// <summary>更新「开机自启」勾选状态。</summary>
    public void SetStartupState(bool enabled)
    {
        _startupItem.Checked = enabled;
    }

    public void ShowBalloon(string title, string text, ToolTipIcon kind)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = kind;
        _icon.ShowBalloonTip(5000);
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // 退出流程中重复释放不应抛给上层
        }
    }
}
