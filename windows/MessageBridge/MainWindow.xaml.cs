using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MessageBridge.Core;

namespace MessageBridge;

public partial class MainWindow : Window
{
    /// <summary>门铃：重复启动 MessageBridge.exe 时由新进程报警，这里把窗口叫出来。</summary>
    private static readonly string ActivateBellName = "MessageBridge.MainWindow.Activate.Slot";

    private readonly MessageStore _store;
    private readonly BridgeServer _server;
    private readonly int _port;
    private EventWaitHandle? _bell;
    private Thread? _bellThread;

    public MainWindow(MessageStore store, int port, BridgeServer server)
    {
        InitializeComponent();
        _store = store;
        _server = server;
        _port = port;
        UpdateCount();

        MessageList.ItemsSource = store.Messages;

        // 新消息到达时刷新计数（列表由 ObservableCollection 自动更新）
        store.Messages.CollectionChanged += (_, _) => UpdateCount();

        // 手机连上/断开时刷新右上角状态
        server.Connected += _ => SafeUpdateStatus();
        server.Disconnected += _ => SafeUpdateStatus();
        UpdateStatus();
        StartActivateListener();
    }

    /// <summary>后台线程回调里刷新状态：退出过程中 Dispatcher 已关闭时直接跳过。</summary>
    private void SafeUpdateStatus()
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.InvokeAsync(UpdateStatus);
        }
        catch
        {
            // 退出阶段忽略
        }
    }

    /// <summary>刷新「监听端口 · 连接状态」文字。</summary>
    public void UpdateStatus()
    {
        var conn = _server.Connections.FirstOrDefault();
        if (conn == null)
        {
            PortText.Text = $"监听端口：{_port} · 等待手机连接";
            return;
        }

        var name = string.IsNullOrEmpty(conn.DeviceName)
            ? conn.RemoteIp
            : $"{conn.DeviceName}（{conn.RemoteIp}）";
        PortText.Text = $"监听端口：{_port} · 已连接 {name}";
    }

    private void UpdateCount()
    {
        CountText.Text = $"最近 {_store.Messages.Count} 条 · 最多保留 {MessageStore.MaxCount} 条";
    }

    private void OnClearClicked(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show("确定清空全部消息记录？此操作不可恢复。",
            "Message", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            _store.Clear();
        }
    }

    /// <summary>把窗口叫到最前面（静默启动后的正常打开方式）。</summary>
    public void BringToFront()
    {
        // 静默启动时窗口处在最小化状态，先还原再加回任务栏，顺序错了会变成一个角落里的空壳
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        ShowInTaskbar = true; // 手动打开后回到任务栏，方便再次切换
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

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

                if (Dispatcher.HasShutdownStarted) return;
                Dispatcher.InvokeAsync(BringToFront);
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
    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        StopActivateListener();
        base.OnClosed(e);
    }
}
