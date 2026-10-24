using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MessageBridge.Core;

namespace MessageBridge;

/// <summary>
/// 托盘左键弹出的「实时通知快照面板」。
/// 独立于 messages.json 历史：这里只显示手机此刻还挂着的通知，断连即清空。
///
/// 交互：弹出时正常抢焦点（用户点托盘就是想在面板上操作），
/// 所以「点到别处 → 失焦自动收起」就能直接工作，Mica 背景在有焦点时也正常显示。
/// 另外还有 × 按钮、Esc、再点一次托盘图标 三种收起方式。
/// </summary>
public sealed partial class SnapshotWindow : Window
{
    private const int DesignWidth = 360;
    /// <summary>最大高度：通知再多就滚动。</summary>
    private const int MaxDesignHeight = 520;
    /// <summary>标题 + 开关行 + 底栏这些固定部分占的高度。</summary>
    private const int ChromeHeight = 116;
    /// <summary>单条通知大概占的高度（图标 48 + 两行文字 + 留白）。</summary>
    private const int ItemHeight = 78;

    /// <summary>面板里改了「电脑气泡」开关。</summary>
    public event Action<bool>? ToastSwitchToggled;
    /// <summary>面板里改了「手机勿扰」开关：0=开启勿扰，2=关闭勿扰。</summary>
    public event Action<int>? InterruptionFilterToggled;
    /// <summary>点了「清空手机通知」。</summary>
    public event Action? ClearRequested;

    /// <summary>程序化设置控件状态时不触发事件。</summary>
    private bool _suppressEvents;

    /// <summary>刚显示时先别自动收起（避免还没拿到焦点就自己关了）。</summary>
    private bool _allowAutoHide;

    /// <summary>窗口尺寸已经按当前 DPI 摆过一次。</summary>
    private bool _sized;

    /// <summary>第一次显示走正常的 Show（之后 DPI 才读得准）。</summary>
    private bool _shownOnce;

    /// <summary>
    /// 「刚弹出时别自己收起」的守卫定时器。
    /// 必须存成字段：之前写成局部变量，方法返回后没有强引用，
    /// DispatcherQueueTimer 被 GC 回收 → _allowAutoHide 永远是 false → 点外面根本不收。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _showGuardTimer;

    /// <summary>点面板外面的兜底检测（有些窗口被点到时不会真正抢焦点，光靠失焦会漏）。</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _clickWatchTimer;

    public SnapshotWindow(SnapshotStore store, Window? owner)
    {
        InitializeComponent();
        _ = owner; // 目前不需要 owner，保留参数便于以后做模态化

        SnapshotList.ItemsSource = store.Items;
        store.Changed += UpdateEmptyState;
        UpdateEmptyState();

        Title = "手机实时通知";
        SystemBackdrop = new MicaBackdrop();
        SetupChrome();
        Activated += OnActivated;
    }

    /// <summary>去掉系统边框和标题栏，固定置顶、不可缩放、不进 Alt+Tab。</summary>
    private void SetupChrome()
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        AppWindow.IsShownInSwitchers = false;

        // WinUI 3 的无边框窗口不会自动获得 Win11 的圆角，手动让 DWM 裁一下
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            var pref = DwmWindowCornerPreference.Round;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference,
                ref pref, Marshal.SizeOf<DwmWindowCornerPreference>());

            // Win11 会给每个窗口画 1px 边框（默认浅色）。深色面板上那圈就是所谓的「白边」，
            // 用 DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE 关掉它。
            var noBorder = DwmColorNone;
            DwmSetWindowAttribute(hwnd, DwmWindowAttribute.BorderColor,
                ref noBorder, Marshal.SizeOf<uint>());
        }
        catch
        {
            // 圆角/边框是锦上添花，失败就保持默认
        }
    }

    private void UpdateEmptyState()
    {
        var empty = SnapshotList.Items.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // 条目增减时窗口高度跟着变（只在已经显示时调整，避免隐藏状态下乱动）
        if (_sized && AppWindow.IsVisible) PlaceAtTray();
    }

    /// <summary>刷新顶部开关与可用状态（断连时远程开关置灰）。</summary>
    public void SetState(bool connected, bool toastEnabled, bool dndOn, bool canSetDnd)
    {
        _suppressEvents = true;
        try
        {
            ToastSwitch.IsEnabled = connected;
            ToastSwitch.IsChecked = toastEnabled;

            DndSwitch.IsEnabled = connected && canSetDnd;
            DndSwitch.IsChecked = dndOn;

            ClearButton.IsEnabled = connected;
            StatusText.Text = connected ? "已连接" : "未连接";

            // 底部一行说清楚当前为什么不给点，别让人以为开关坏了
            FooterText.Text = !connected
                ? "手机未连接"
                : canSetDnd
                    ? "点面板外面或按 Esc 收起"
                    : "手机未授予「勿扰访问权限」，勿扰开关不可用";

            // 手机没给「勿扰访问权限」时开关是灰的，说清楚为什么，别让人以为点坏了
            ToolTipService.SetToolTip(DndSwitch, canSetDnd
                ? "开启后手机不再响铃，通知仍会同步到电脑"
                : "手机没有授予「勿扰访问权限」，打开手机上的 Message，到权限列表里授予即可");
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    public bool IsShowing => AppWindow.IsVisible;

    /// <summary>启动时先离屏显示再藏起来，让 XAML 内容和 DPI 就绪，第一次点托盘不闪。</summary>
    public void Prewarm()
    {
        if (_shownOnce) return;
        Services.WindowScale.SetBounds(this, -8000, -8000, 400, 400);
        AppWindow.Show();
        AppWindow.Hide();
        _shownOnce = true;
    }

    /// <summary>显示/收起（托盘左键用这个做开关）。</summary>
    public void Toggle()
    {
        if (AppWindow.IsVisible) Hide();
        else ShowAtTray();
    }

    public void Hide()
    {
        _allowAutoHide = false;
        AppWindow.Hide();
    }

    /// <summary>贴着任务栏右下角弹出。</summary>
    public void ShowAtTray()
    {
        // 先 Show 再量尺寸：窗口没显示过时 GetDpiForWindow 只返回 96
        AppWindow.Show();
        Activate();

        PlaceAtTray();
        // WinUI 在首次激活后会按它记的旧尺寸再摆一次窗口，等布局跑完再摆一次才稳
        App.Ui.TryEnqueue(PlaceAtTray);

        // 显示后短暂窗口期内不自动收起，之后才允许「点击外部关闭」
        _allowAutoHide = false;
        if (_showGuardTimer == null)
        {
            _showGuardTimer = App.Ui.CreateTimer();
            _showGuardTimer.Interval = TimeSpan.FromMilliseconds(220);
            _showGuardTimer.Tick += (_, _) =>
            {
                _showGuardTimer?.Stop();
                _allowAutoHide = true;
            };
        }
        _showGuardTimer.Stop();
        _showGuardTimer.Start();

        StartClickWatch();
    }

    /// <summary>
    /// 失焦收起是主力，但点到桌面、游戏这类不抢焦点的窗口时 Deactivated 不会来，
    /// 所以再加一道：鼠标在面板外面按下左键 → 直接收。
    /// </summary>
    private void StartClickWatch()
    {
        if (_clickWatchTimer == null)
        {
            _clickWatchTimer = App.Ui.CreateTimer();
            _clickWatchTimer.Interval = TimeSpan.FromMilliseconds(120);
            _clickWatchTimer.Tick += (_, _) =>
            {
                if (!AppWindow.IsVisible)
                {
                    _clickWatchTimer.Stop();
                    return;
                }

                // 刚弹出来的前 400ms 不理会，否则托盘那一下点击会被当成「点外面」
                if (!_allowAutoHide) return;

                if (IsCursorOverPanel())
                {
                    // 读掉这次按下记录，免得移出面板后被误判
                    _ = GetAsyncKeyState(VkLeftButton);
                    return;
                }

                if ((GetAsyncKeyState(VkLeftButton) & 0x8001) != 0) Hide();
            };
        }
        _clickWatchTimer.Start();
    }

    private bool IsCursorOverPanel()
    {
        try
        {
            if (!GetCursorPos(out var pt)) return true;
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            if (!GetWindowRect(hwnd, out var rect)) return true;
            return pt.X >= rect.Left && pt.X <= rect.Right &&
                   pt.Y >= rect.Top && pt.Y <= rect.Bottom;
        }
        catch
        {
            return true; // 判断不了就别乱收
        }
    }

    private void PlaceAtTray()
    {
        var scale = Services.WindowScale.For(this);
        var width = Services.WindowScale.Px(DesignWidth, scale);
        var height = Services.WindowScale.Px(ComputeDesignHeight(), scale);

        // DisplayArea.WorkArea 已经是物理像素，和 SetWindowPos 同一套单位
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        // 保持右手边、贴着任务栏，主屏分辨率变化时也自动重新定位
        var x = area.X + area.Width - width - Services.WindowScale.Px(12, scale);
        var y = area.Y + area.Height - height - Services.WindowScale.Px(12, scale);

        Services.WindowScale.SetBounds(this, x, y, width, height);
        // 无边框窗口拿不到 DWM 圆角，必须自己把窗口形状裁成圆角，否则圆角外会露出一圈白底
        Services.WindowScale.ApplyRoundCorners(this, Services.WindowScale.Px(8, scale));
        _sized = true;
    }

    /// <summary>高度跟着条目数走：只有两三条时不留一大片空白，超过上限就滚动。</summary>
    private int ComputeDesignHeight()
    {
        var count = SnapshotList.Items.Count;
        if (count == 0) return 300;
        return Math.Min(MaxDesignHeight, ChromeHeight + count * ItemHeight);
    }

    /// <summary>点到别处（窗口失焦）就收起，这是面板的主力关闭方式。</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _allowAutoHide && _sized)
        {
            AppWindow.Hide();
            return;
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Hide();

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Hide();
    }

    private void OnToastChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ToastSwitchToggled?.Invoke(ToastSwitch.IsChecked == true);
    }

    /// <summary>
    /// 勾选 = 开勿扰，取消 = 关勿扰。
    /// 用 Checked / Unchecked 两个事件分别传固定值，不去读 IsChecked ——
    /// 读属性会受事件触发时机影响，之前就踩到过「勾选反而发了关闭」的坑。
    /// </summary>
    private void OnDndChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        InterruptionFilterToggled?.Invoke(0);
    }

    private void OnDndUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        InterruptionFilterToggled?.Invoke(2);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ClearRequested?.Invoke();
    }

    // ---- DWM 圆角 ----

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33,
        BorderColor = 34
    }

    private enum DwmWindowCornerPreference
    {
        Round = 2
    }

    /// <summary>DWMWA_COLOR_NONE：让 DWM 不画那圈 1px 窗口边框。</summary>
    private const uint DwmColorNone = 0xFFFFFFFF;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, DwmWindowAttribute attribute, ref DwmWindowCornerPreference value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, DwmWindowAttribute attribute, ref uint value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VkLeftButton = 0x01;
}
