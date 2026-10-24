using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MessageBridge.Core;

namespace MessageBridge;

/// <summary>
/// 托盘左键弹出的「实时通知快照面板」。
/// 独立于 messages.json 历史：这里只显示手机此刻还挂着的通知，断连即清空。
/// 无边框 + 亚克力背景 + 置顶；关闭按钮 / Esc / 点击外部 / 再点托盘图标 都能收起。
/// </summary>
public sealed partial class SnapshotWindow : Window
{
    private const int DesignWidth = 400;
    /// <summary>最大高度：通知再多就滚动。</summary>
    private const int MaxDesignHeight = 540;
    /// <summary>标题 + 开关行 + 底栏这些固定部分占的高度。</summary>
    private const int ChromeHeight = 122;
    /// <summary>单条通知大概占的高度（两行内容 + 留白）。</summary>
    private const int ItemHeight = 84;

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

    /// <summary>窗口在这一轮显示期间是否被真正激活过（用户点过面板）。</summary>
    private bool _everActivated;

    /// <summary>第一次显示走正常的 Show（之后 DPI 才读得准）。</summary>
    private bool _shownOnce;

    /// <summary>
    /// 鼠标离开面板多久后自动收起。
    /// 因为弹出时特意不抢焦点，「失焦收起」在用户还没碰过面板时不会触发，
    /// 所以再加一道「鼠标移开就收」的保险，避免面板一直挂在屏幕上收不掉。
    /// </summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoHideTimer;
    private DateTime _shownAt;
    private int _outsideTicks;
    /// <summary>这一轮显示里鼠标是否进过面板（进过才按「移开就收」处理）。</summary>
    private bool _hoveredPanel;

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
        }
        catch
        {
            // 圆角是锦上添花，失败就保持直角
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
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    public bool IsShowing => AppWindow.IsVisible;

    /// <summary>
    /// 启动时先离屏显示再藏起来：让 XAML 内容和 DPI 都就绪。
    /// 这样用户第一次点托盘时就能直接走「不抢焦点」的显示路径，不会把游戏顶掉。
    /// </summary>
    public void Prewarm()
    {
        if (_shownOnce) return;
        Services.WindowScale.SetBounds(this, -8000, -8000, 400, 400);
        AppWindow.Show();
        AppWindow.Hide();
        _shownOnce = true;
        _everActivated = false;
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
        _everActivated = false;
        _outsideTicks = 0;
        _autoHideTimer?.Stop();
        AppWindow.Hide();
    }

    /// <summary>
    /// 贴着任务栏右下角弹出。
    /// 关键：显示时不抢前台焦点（否则会把全屏游戏/正在用的窗口顶掉），
    /// 所以只有用户点过面板、它真正被激活过之后，「点外面自动收起」才会生效。
    /// </summary>
    public void ShowAtTray()
    {
        if (!_shownOnce)
        {
            // 第一次必须正常 Show 一次，之后 GetDpiForWindow 才会返回真实值
            AppWindow.Show();
            _shownOnce = true;
            _everActivated = false;
        }
        else
        {
            Services.WindowScale.ShowNoActivate(this);
        }

        PlaceAtTray();
        // WinUI 在首次激活后会按它记的旧尺寸再摆一次窗口，等布局跑完再摆一次才稳
        App.Ui.TryEnqueue(PlaceAtTray);

        // 显示后短暂窗口期内不自动收起，之后才允许「点击外部关闭」
        _allowAutoHide = false;
        _shownAt = DateTime.Now;
        _outsideTicks = 0;
        _hoveredPanel = false;
        var timer = App.Ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(220);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _allowAutoHide = true;
        };
        timer.Start();

        StartAutoHideWatch();
    }

    /// <summary>
    /// 每 250ms 看一眼鼠标在不在面板上。
    /// 鼠标进过面板再移开 → 2 秒后收起；
    /// 鼠标一直没进过面板（用户只是想扫一眼、鼠标还在托盘上）→ 30 秒后收起，
    /// 免得它一直赖在屏幕上收不掉。
    /// </summary>
    private void StartAutoHideWatch()
    {
        if (_autoHideTimer == null)
        {
            _autoHideTimer = App.Ui.CreateTimer();
            _autoHideTimer.Interval = TimeSpan.FromMilliseconds(250);
            _autoHideTimer.Tick += (_, _) =>
            {
                if (!AppWindow.IsVisible)
                {
                    _autoHideTimer.Stop();
                    return;
                }

                var elapsed = (DateTime.Now - _shownAt).TotalSeconds;

                if (IsCursorOverPanel())
                {
                    _hoveredPanel = true;
                    _outsideTicks = 0;
                    return;
                }

                // 刚弹出来的前 2 秒给用户反应时间
                if (elapsed < 2) return;

                if (!_hoveredPanel)
                {
                    if (elapsed >= 30) Hide();
                    return;
                }

                if (++_outsideTicks >= 8) Hide();
            };
        }
        _autoHideTimer.Start();
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
        _sized = true;
    }

    /// <summary>高度跟着条目数走：只有两三条时不留一大片空白，超过上限就滚动。</summary>
    private int ComputeDesignHeight()
    {
        var count = SnapshotList.Items.Count;
        if (count == 0) return 300;
        return Math.Min(MaxDesignHeight, ChromeHeight + count * ItemHeight);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            // 用户点了面板（或首次显示抢到焦点），之后失焦才该收起
            _everActivated = true;
            return;
        }

        if (_allowAutoHide && _everActivated && _sized)
        {
            Hide();
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

    private void OnDndChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        InterruptionFilterToggled?.Invoke(DndSwitch.IsChecked == true ? 0 : 2);
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        ClearRequested?.Invoke();
    }

    // ---- DWM 圆角 ----

    private enum DwmWindowAttribute
    {
        WindowCornerPreference = 33
    }

    private enum DwmWindowCornerPreference
    {
        Round = 2
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, DwmWindowAttribute attribute, ref DwmWindowCornerPreference value, int size);

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
}
