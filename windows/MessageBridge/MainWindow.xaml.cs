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
/// 历史消息主窗口（WinUI 3 + Mica 背景 + 自定义标题栏）。
/// 关闭时隐藏到托盘，不退出进程。
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>门铃：重复启动 MessageBridge.exe 时由新进程报警，这里把窗口叫出来。</summary>
    private static readonly string ActivateBellName = "MessageBridge.MainWindow.Activate.Slot.WinUI";

    private const int DesignWidth = 940;
    private const int DesignHeight = 640;

    private readonly MessageStore _store;
    private readonly BridgeServer _server;
    private readonly int _port;
    private EventWaitHandle? _bell;
    private Thread? _bellThread;
    private bool _sized;

    public MainWindow(MessageStore store, int port, BridgeServer server)
    {
        InitializeComponent();
        _store = store;
        _server = server;
        _port = port;

        Title = "Message · 手机消息推送";
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        MessageList.ItemsSource = store.Messages;
        UpdateCount();
        UpdateStatus();

        // 新消息到达时刷新计数（列表由 ObservableCollection 自动更新）
        store.Messages.CollectionChanged += (_, _) => UpdateCount();

        // 手机连上/断开时刷新右上角状态
        server.Connected += _ => SafeUpdateStatus();
        server.Disconnected += _ => SafeUpdateStatus();

        AppWindow.Closing += OnWindowClosing;
        Closed += Window_Closed;
        Activated += OnFirstActivated;
        StartActivateListener();
    }

    /// <summary>第一次显示时按屏幕缩放把窗口调成设计尺寸（150% 屏上不能直接写 940x640 物理像素）。</summary>
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

    /// <summary>把窗口叫到最前面（静默启动后的正常打开方式，也用于托盘双击）。</summary>
    public void ShowFromTray()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
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

                App.Ui?.TryEnqueue(ShowFromTray);
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
