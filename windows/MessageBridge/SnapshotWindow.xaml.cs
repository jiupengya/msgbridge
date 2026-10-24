using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using MessageBridge.Core;
using Windows.Graphics;

namespace MessageBridge;

/// <summary>
/// 托盘左键弹出的「实时通知快照面板」。
/// 独立于 messages.json 历史：这里只显示手机此刻还挂着的通知，断连即清空。
/// 无边框 + 置顶，点击窗口外部（失去激活）自动收起。
/// </summary>
public sealed partial class SnapshotWindow : Window
{
    private const int DesignWidth = 390;
    private const int DesignHeight = 520;

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

    /// <summary>上一次的显示区域，用来判断窗口位置是否需要重新计算。</summary>
    private bool _sized;

    public SnapshotWindow(SnapshotStore store, Window? owner)
    {
        InitializeComponent();
        _ = owner; // 目前不需要 owner，保留参数便于以后做模态化

        SnapshotList.ItemsSource = store.Items;
        store.Changed += UpdateEmptyState;
        UpdateEmptyState();

        Title = "手机实时通知";
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
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
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

    /// <summary>贴着任务栏右下角弹出。</summary>
    public void ShowAtTray()
    {
        // 必须先 Show/Activate：窗口没显示过时 GetDpiForWindow 只返回 96，
        // 按这个算尺寸会小一圈（150% 屏上只有 2/3 大）。
        AppWindow.Show();
        Activate();

        PlaceAtTray();
        // WinUI 在首次激活后会按它记的旧尺寸再摆一次窗口，等布局跑完再摆一次才稳
        App.Ui.TryEnqueue(PlaceAtTray);

        // 显示后短暂窗口期内不自动收起，之后才允许「点击外部关闭」
        _allowAutoHide = false;
        var timer = App.Ui.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(260);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _allowAutoHide = true;
        };
        timer.Start();
    }

    private void PlaceAtTray()
    {
        var scale = Services.WindowScale.For(this);
        var width = Services.WindowScale.Px(DesignWidth, scale);
        var height = Services.WindowScale.Px(DesignHeight, scale);

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        // 保持右手边、贴着任务栏，主屏分辨率变化时也自动重新定位
        var x = area.X + area.Width - width - Services.WindowScale.Px(12, scale);
        var y = area.Y + area.Height - height - Services.WindowScale.Px(12, scale);

        Services.WindowScale.SetBounds(this, x, y, width, height);
        _sized = true;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && _allowAutoHide && _sized)
        {
            AppWindow.Hide();
        }
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
}
