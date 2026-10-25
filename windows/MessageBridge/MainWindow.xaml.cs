using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MessageBridge.Core;
using Windows.Graphics;

namespace MessageBridge;

/// <summary>
/// 主窗口：上面是「实时通知」（手机此刻还挂着的通知 + 两个开关），
/// 下面是「历史记录」（messages.json）。用一个 SelectorBar 切换。
/// 关闭时隐藏到托盘，不退出进程。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>门铃：重复启动 MessageBridge.exe 时由新进程报警，这里把窗口叫出来。</summary>
    private static readonly string ActivateBellName = "MessageBridge.MainWindow.Activate.Slot.WinUI";

    private const int DesignWidth = 940;
    private const int DesignHeight = 700;

    private readonly MessageStore _store;
    private readonly SnapshotStore _snapshot;
    private readonly BridgeServer _server;
    private readonly int _port;
    private EventWaitHandle? _bell;
    private Thread? _bellThread;
    private bool _sized;

    /// <summary>程序化设置开关状态时不触发事件。</summary>
    private bool _suppressLiveEvents;

    /// <summary>实时页里改了「电脑气泡」开关。</summary>
    public event Action<bool>? ToastSwitchToggled;
    /// <summary>实时页里改了「手机勿扰」开关：0=开启勿扰，2=关闭勿扰。</summary>
    public event Action<int>? InterruptionFilterToggled;
    /// <summary>点了「清空手机通知」。</summary>
    public event Action? ClearPhoneNotificationsRequested;

    public MainWindow(MessageStore store, SnapshotStore snapshot, int port, BridgeServer server)
    {
        InitializeComponent();
        _store = store;
        _snapshot = snapshot;
        _server = server;
        _port = port;

        Title = "Message · 手机消息推送";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        MessageList.ItemsSource = store.Messages;
        SnapshotList.ItemsSource = snapshot.Items;

        UpdateCount();
        UpdateStatus();
        UpdateLiveEmptyState();

        // 新消息到达时刷新计数（列表由 ObservableCollection 自动更新）
        store.Messages.CollectionChanged += (_, _) => UpdateCount();
        snapshot.Changed += UpdateLiveEmptyState;

        // 手机连上/断开时刷新右上角状态
        server.Connected += _ => SafeUpdateStatus();
        server.Disconnected += _ => SafeUpdateStatus();

        AppWindow.Closing += OnWindowClosing;
        Closed += Window_Closed;
        Activated += OnFirstActivated;
        StartActivateListener();
    }

    /// <summary>第一次显示时按屏幕缩放把窗口调成设计尺寸（150% 屏上不能直接写 940x700 物理像素）。</summary>
    private void OnFirstActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_sized) return;
        _sized = true;
        ResizeToDesign();
    }

    private void ResizeToDesign()
    {
        var scale = Services.WindowScale.For(this);
        var width = Services.WindowScale.Px(DesignWidth, scale);
        var height = Services.WindowScale.Px(DesignHeight, scale);

        // DisplayArea.WorkArea 已经是物理像素，和 SetWindowPos 同一套单位
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;

        Services.WindowScale.SetBounds(this,
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width, height);
    }

    /// <summary>后台线程回调里刷新状态（窗口关闭中直接跳过）。</summary>
    private void SafeUpdateStatus()
    {
        App.Ui?.TryEnqueue(UpdateStatus);
    }

    /// <summary>刷新「监听端口 · 连接状态」文字。</summary>
    public void UpdateStatus()
    {
        var conn = _server.Connections.FirstOrDefault();
        if (conn == null)
        {
            PortText.Text = $"监听端口 {_port} · 等待手机连接";
            return;
        }

        var name = string.IsNullOrEmpty(conn.DeviceName)
            ? conn.RemoteIp
            : $"{conn.DeviceName}（{conn.RemoteIp}）";
        PortText.Text = $"监听端口 {_port} · 已连接 {name}";
    }

    private void UpdateCount()
    {
        CountText.Text = $"最近 {_store.Messages.Count} 条 · 最多保留 {MessageStore.MaxCount} 条";
    }

    // ---- 实时通知 ----

    private void UpdateLiveEmptyState()
    {
        var empty = SnapshotList.Items.Count == 0;
        LiveEmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>刷新实时页的开关与可用状态（断连时远程开关置灰）。</summary>
    public void SetLiveState(bool connected, bool toastEnabled, bool dndOn, bool canSetDnd)
    {
        _suppressLiveEvents = true;
        try
        {
            ToastSwitch.IsEnabled = connected;
            ToastSwitch.IsChecked = toastEnabled;

            DndSwitch.IsEnabled = connected && canSetDnd;
            DndSwitch.IsChecked = dndOn;

            ClearPhoneButton.IsEnabled = connected;
            LiveEmptyText.Text = connected ? "手机当前没有通知" : "手机未连接";

            // 手机没给「勿扰访问权限」时开关是灰的，说清楚为什么，别让人以为点坏了
            ToolTipService.SetToolTip(DndSwitch, canSetDnd
                ? "开启后手机不再响铃，通知仍会同步到电脑"
                : "手机没有授予「勿扰访问权限」，打开手机上的 Message，到权限列表里授予即可");
        }
        finally
        {
            _suppressLiveEvents = false;
        }
    }

    private void OnToastChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveEvents) return;
        ToastSwitchToggled?.Invoke(true);
    }

    private void OnToastUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveEvents) return;
        ToastSwitchToggled?.Invoke(false);
    }

    private void OnDndChecked(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveEvents) return;
        InterruptionFilterToggled?.Invoke(0);
    }

    private void OnDndUnchecked(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveEvents) return;
        InterruptionFilterToggled?.Invoke(2);
    }

    private void OnClearPhoneClicked(object sender, RoutedEventArgs e)
    {
        if (_suppressLiveEvents) return;
        ClearPhoneNotificationsRequested?.Invoke();
    }

    // ---- 视图切换 ----

    private void OnViewSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var live = ReferenceEquals(sender.SelectedItem, LiveTab);
        LiveView.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        HistoryView.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>切到指定页（托盘左键打开时默认落在实时通知）。</summary>
    public void SelectLive(bool live)
    {
        ViewSelector.SelectedItem = live ? LiveTab : HistoryTab;
    }

    // ---- 历史记录 ----

    private async void OnClearClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "清空全部消息记录？",
            Content = "此操作不可恢复，通知图标也会一并清理。",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _store.Clear();
        }
    }

    /// <summary>把窗口叫到最前面（静默启动后的正常打开方式，也用于托盘点击）。</summary>
    public void ShowFromTray(bool live = false)
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        SelectLive(live);
        Activate();

        // Activate 在从托盘唤起时不总能拿到前台焦点，补一次 Win32 调用
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(AppWindow.Id);
            SetForegroundWindow(hwnd);
        }
        catch
        {
            // 拿不到前台焦点不影响窗口显示
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // ---- 重复启动唤醒 ----

    /// <summary>
    /// 后台线程守着门铃：用户再次运行 MessageBridge.exe 时，第二个进程
    /// （见 <see cref="App.ShowRunningInstance"/>）敲响门铃，这里接住并把窗口叫出来。
    /// 用事件而不是 Mutex：Mutex 的所有权只能转移给 WaitOne 的线程，
    /// 别的进程 ReleaseMutex 会直接抛「从不同步的代码块中调用了对象同步方法」。
    /// </summary>
    private void StartActivateListener()
    {
        try
        {
            _bell = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateBellName, out _);
        }
        catch
        {
            return; // 唤醒功能不可用不影响主流程
        }

        _bellThread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!_bell!.WaitOne()) continue;
                }
                catch (ObjectDisposedException)
                {
                    return; // 窗口已关闭
                }

                App.Ui?.TryEnqueue(() => ShowFromTray(live: true));
            }
        })
        {
            IsBackground = true,
            Name = "MessageBridge.ActivateListener"
        };
        _bellThread.Start();
    }

    private void StopActivateListener()
    {
        try
        {
            _bell?.Dispose();
        }
        catch
        {
            // 退出阶段忽略
        }
        _bell = null;
    }

    // 关闭窗口 -> 最小化到托盘
    private void OnWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
    }

    private void Window_Closed(object sender, WindowEventArgs args)
    {
        StopActivateListener();
    }
}
