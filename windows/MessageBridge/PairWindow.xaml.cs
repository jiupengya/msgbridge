using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MessageBridge.Core;

namespace MessageBridge;

/// <summary>
/// 手机发来配对请求时弹出的窗口：显示对方设备信息和 PIN 码。
/// WinUI 3 的窗口不能像 WPF 那样 SizeToContent，这里固定高度、内容自适应。
/// </summary>
public sealed partial class PairWindow : Window
{
    private const int DesignWidth = 430;
    private const int DesignHeight = 330;

    /// <summary>用户点击「拒绝」时的回调。</summary>
    public Action? Rejected { get; set; }

    public PairWindow()
    {
        InitializeComponent();
        Title = "Message · 配对请求";

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsAlwaysOnTop = true;
        }
    }

    public void ShowPair(PendingPair pending)
    {
        DeviceText.Text = $"来自：{pending.DeviceName}\nIP：{pending.RemoteIp}";
        PinText.Text = pending.Pin;

        AppWindow.Show();
        Activate();

        // 先显示再算尺寸：窗口没显示过时 DPI 读不到真实值
        var scale = Services.WindowScale.For(this);
        var width = Services.WindowScale.Px(DesignWidth, scale);
        var height = Services.WindowScale.Px(DesignHeight, scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        Services.WindowScale.SetBounds(this,
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);

        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
            // 没有音频设备时忽略
        }
    }

    public void HidePair() => AppWindow.Hide();

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        Rejected?.Invoke();
        HidePair();
    }
}
