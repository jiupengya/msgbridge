using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MessageBridge.Core;
using MessageBridge.Services;

namespace MessageBridge;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private Tray? _tray;
    private ToastBridge? _toast;
    private BridgeServer? _server;
    private MessageRouter? _router;
    private TrustStore? _trust;
    private MessageStore? _store;
    private IconStore? _icons;
    private SnapshotStore? _snapshot;
    private MainWindow? _mainWindow;
    private SnapshotWindow? _snapshotWindow;
    private PairWindow? _pairWindow;
    private string _dataDir = "";
    private string _configPath = "";

    /// <summary>连续通知合并窗口：这段时间内到达的消息合并成「N 条新消息」。</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);
    private readonly List<MessageItem> _recent = new();

    /// <summary>暂停推送的截止时间（null 表示未暂停）。</summary>
    private DateTime? _pauseUntil;
    private DispatcherTimer? _pauseTimer;

    // ---- 两个独立开关的状态 ----

    /// <summary>电脑是否弹出系统 Toast（持久化到 config.json，两端都能改）。</summary>
    private bool _enableWindowsToast = true;

    /// <summary>手机当前是否处于勿扰（由手机上报）。</summary>
    private bool _dndOn;

    /// <summary>手机是否授予了修改勿扰的权限；false 时电脑端勿扰开关置灰。</summary>
    private bool _canSetDnd;

    /// <summary>手机是否已配对连接（断连时快照清空、远程开关置灰）。</summary>
    private bool _phoneConnected;

    private sealed class Config
    {
        public int Port { get; set; } = 17001;

        /// <summary>是否弹出 Windows 系统通知气泡。</summary>
        public bool EnableWindowsToast { get; set; } = true;
    }

    private Config _config = new();

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private bool IsPaused => _pauseUntil.HasValue && DateTime.Now < _pauseUntil.Value;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // 兜底：任何未处理异常都写一份日志，避免以后只有系统弹窗、没有线索
        DispatcherUnhandledException += (_, args) => CrashLog(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => CrashLog(args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog(args.Exception);
            args.SetObserved();
        };

        _singleInstance = new Mutex(true, "MessageBridge.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 静默启动：重复启动不再弹「已在运行」对话框，直接把已在运行的实例叫出来
            ShowRunningInstance();
            Shutdown();
            return;
        }

        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MessageBridge");
        Directory.CreateDirectory(_dataDir);
        _configPath = Path.Combine(_dataDir, "config.json");
        LoadConfig();

        _icons = new IconStore(_dataDir);
        _store = new MessageStore(_dataDir, _icons);
        _snapshot = new SnapshotStore();
        // 快照面板引用的图标不属于历史记录，裁剪时要一起保留
        _store.ExtraIconKeep = () => _snapshot.IconFiles;
        _trust = new TrustStore(_dataDir);
        AumidShortcut.EnsureRegistered(); // 系统 Toast 需要 AUMID 身份（开始菜单快捷方式）
        _toast = new ToastBridge(_dataDir);
        _server = new BridgeServer { Port = _config.Port };
        _router = new MessageRouter(_server, _trust);

        _router.PairRequested += OnPairRequested;
        _router.PairResult += OnPairResult;
        _router.MessageReceived += OnMessageReceived;
        _router.NotificationChanged += OnNotificationChanged;
        _router.InterruptionFilterChanged += OnInterruptionFilterChanged;
        _router.WindowsToastRequested += OnWindowsToastRequested;

        _tray = new Tray(LoadTrayIconStream());
        _tray.OpenRequested += ShowMainWindow;
        _tray.SnapshotRequested += ShowSnapshotPanel;
        _tray.ExitRequested += OnTrayExit;
        _tray.PauseToggleRequested += OnPauseToggle;
        _tray.StartupToggleRequested += OnStartupToggle;
        _tray.SetStartupState(StartupRegistry.IsEnabled());
        _tray.SetPauseState(false, "暂停推送 30 分钟");

        _server.Disconnected += OnServerDisconnected;

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"端口 {_config.Port} 监听失败：{ex.Message}\n请检查端口是否被占用。",
                "Message", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _mainWindow = new MainWindow(_store, _config.Port, _server);
        // 静默启动：默认不显示主窗口，只常驻托盘。
        // 看完历史消息从托盘右键「打开 Message」或双击托盘图标打开。
        ApplySilentStartup();

        // 调试用：启动后自动弹一次快照面板（托盘图标在溢出区时无法用自动化点击）
        if (Environment.GetEnvironmentVariable("MSGBRIDGE_SHOW_SNAPSHOT") == "1")
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                ShowSnapshotPanel();
            };
            t.Start();
        }
    }

    // ---- 配置 ----

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(_configPath), ConfigJson);
                if (cfg != null) _config = cfg;
            }
        }
        catch
        {
            // 配置损坏就用默认值
        }

        if (_config.Port is < 1 or > 65535) _config.Port = 17001;
        _enableWindowsToast = _config.EnableWindowsToast;

        // 首次运行 / 新增字段时把完整配置落一次盘，方便用户直接改
        SaveConfig();
    }

    private void SaveConfig()
    {
        try
        {
            _config.EnableWindowsToast = _enableWindowsToast;
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, ConfigJson));
        }
        catch
        {
            // 写配置失败不影响运行
        }
    }

    /// <summary>
    /// 静默启动：窗口照常创建（消息照收照记），只是不显示，
    /// 并且在 Alt+Tab / 任务栏里都看不到，完全藏进托盘。
    /// </summary>
    private void ApplySilentStartup()
    {
        if (_mainWindow == null) return;
        _mainWindow.ShowInTaskbar = false;
        _mainWindow.WindowState = WindowState.Minimized;
        _mainWindow.ShowActivated = false;
        // 先 Show 再 Hide：保证 Window 句柄与绑定都已就绪，之后从托盘打开是即时的
        _mainWindow.Show();
        _mainWindow.Hide();
    }

    /// <summary>重复启动时唤醒已运行的实例（主窗口最小化操作固定在主窗口里处理）。</summary>
    private static void ShowRunningInstance()
    {
        const string slotName = "MessageBridge.MainWindow.Activate.Slot";

        // 这里只应该「有另一个实例在运行」。Mutex 的所有权只会转移给 WaitOne 的线程，
        // ReleaseMutex 必须由持有它的线程调用，所以本进程不碰 Mutex 本身，
        // 而是用一个同名 EventWaitHandle 当门铃，由主实例的监听线程去唤醒窗口。
        try
        {
            if (EventWaitHandle.TryOpenExisting(slotName, out var bell))
            {
                using (bell)
                {
                    bell.Set(); // 拉起一个信号，主实例监听线程接住后显示窗口
                }
            }
        }
        catch
        {
            // 主实例正在退出等异常情况，忽略
        }
    }

    private Stream LoadTrayIconStream()
    {
        var uri = new Uri("pack://application:,,,/Resources/msgicon.ico", UriKind.Absolute);
        var sri = System.Windows.Application.GetResourceStream(uri);
        if (sri == null) throw new InvalidOperationException("缺少托盘图标资源");
        return sri.Stream;
    }

    private void ShowMainWindow()
    {
        _mainWindow?.BringToFront();
    }

    private void OnTrayExit()
    {
        _store?.Flush();
        // 资源统一在 OnExit 里释放：之前两边各 Dispose 一次，
        // 第二次对已释放的 CancellationTokenSource 调 Cancel() 会直接崩掉进程。
        Shutdown();
    }

    // ---- 实时快照面板 ----

    private void ShowSnapshotPanel()
    {
        if (_snapshot == null) return;
        if (_snapshotWindow == null)
        {
            _snapshotWindow = new SnapshotWindow(_snapshot);
            _snapshotWindow.ToastSwitchToggled += OnLocalToastToggle;
            _snapshotWindow.InterruptionFilterToggled += OnLocalDndToggle;
            _snapshotWindow.ClearRequested += OnClearPhoneNotifications;
        }

        RefreshSnapshotPanel();
        _snapshotWindow.ShowAtTray();
    }

    private void RefreshSnapshotPanel()
    {
        _snapshotWindow?.SetState(
            _phoneConnected,
            _enableWindowsToast,
            _dndOn,
            _canSetDnd);
    }

    private void OnNotificationChanged(NotificationUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            if (_snapshot == null || _icons == null) return;
            if (update.IsCancel)
                _snapshot.Remove(update.Key);
            else
                _snapshot.Upsert(update, _icons);
        });
    }

    private void OnInterruptionFilterChanged(int filter, bool canSet)
    {
        Dispatcher.Invoke(() =>
        {
            _dndOn = filter == 0;
            _canSetDnd = canSet;
            RefreshSnapshotPanel();
        });
    }

    /// <summary>手机端拨动了 Windows 气泡开关。</summary>
    private void OnWindowsToastRequested(bool enable)
    {
        Dispatcher.Invoke(() => SetWindowsToast(enable));
    }

    /// <summary>面板上拨动了「电脑气泡」开关。</summary>
    private void OnLocalToastToggle(bool enable)
    {
        SetWindowsToast(enable);
    }

    /// <summary>
    /// 修改 Windows 气泡开关：写配置 → 立即生效 → 把状态回传给手机。
    /// 电脑和手机两个入口最终都走到这里，状态只有一个真相。
    /// </summary>
    private void SetWindowsToast(bool enable)
    {
        _enableWindowsToast = enable;
        SaveConfig();
        _router?.SendWindowsToastState(enable);
        RefreshSnapshotPanel();
    }

    /// <summary>面板上拨动了「手机勿扰」开关：下发指令，等手机上报真实状态。</summary>
    private void OnLocalDndToggle(int filter)
    {
        if (_router?.SendSetInterruptionFilter(filter) != true) return;
        // 乐观更新，手机成功后会回 state.interruptionFilter 纠正
        _dndOn = filter == 0;
        RefreshSnapshotPanel();
    }

    /// <summary>面板上点了「清空手机通知」。</summary>
    private void OnClearPhoneNotifications()
    {
        _router?.SendClearNotifications();
    }

    private void OnServerDisconnected(BridgeConnection conn)
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                _phoneConnected = false;
                _canSetDnd = false;
                _dndOn = false;
                _snapshot?.Clear(); // 断连即销毁快照，重连时手机重新批量补发
                RefreshSnapshotPanel();
            });
        }
        catch
        {
            // 退出阶段 Dispatcher 已关闭
        }
    }

    // ---- 托盘：暂停推送 / 开机自启 ----

    private void OnPauseToggle()
    {
        if (IsPaused)
        {
            _pauseUntil = null;
        }
        else
        {
            _pauseUntil = DateTime.Now.AddMinutes(30);
            _recent.Clear();
        }
        UpdatePauseMenu();
    }

    private void UpdatePauseMenu()
    {
        if (IsPaused && _pauseUntil.HasValue)
        {
            var left = Math.Max(1, (int)Math.Ceiling((_pauseUntil.Value - DateTime.Now).TotalMinutes));
            _tray?.SetPauseState(true, $"恢复推送（还剩 {left} 分钟）");
            if (_pauseTimer == null)
            {
                _pauseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                _pauseTimer.Tick += (_, _) => UpdatePauseMenu();
            }
            _pauseTimer.Start();
        }
        else
        {
            _pauseUntil = null;
            _tray?.SetPauseState(false, "暂停推送 30 分钟");
            _pauseTimer?.Stop();
        }
    }

    private void OnStartupToggle()
    {
        StartupRegistry.Set(!StartupRegistry.IsEnabled());
        _tray?.SetStartupState(StartupRegistry.IsEnabled());
    }

    // ---- 配对 ----

    private void OnPairRequested(PendingPair pending)
    {
        Dispatcher.Invoke(() =>
        {
            _pairWindow ??= new PairWindow { Rejected = () => _router?.CancelPairing(pending.DeviceId) };
            _pairWindow.ShowPair(pending);
        });
    }

    private void OnPairResult(BridgeConnection conn, bool ok)
    {
        Dispatcher.Invoke(() =>
        {
            _pairWindow?.Hide();
            _mainWindow?.UpdateStatus();

            if (ok)
            {
                _phoneConnected = true;
                // 连接建立后把 Windows 气泡开关状态同步给手机
                _router?.SendWindowsToastState(_enableWindowsToast);
                RefreshSnapshotPanel();
            }

            var text = ok
                ? $"已与 {conn.DeviceName} 完成配对"
                : $"与 {conn.DeviceName} 配对失败（PIN 不正确或已过期）";
            ShowToast("Message · 配对", text, null, false);
        });
    }

    // ---- 消息 ----

    private void OnMessageReceived(MessageItem item)
    {
        Dispatcher.Invoke(() =>
        {
            // 图标只解码一次，落盘成 png；之后列表和 Toast 都直接读文件
            if (!string.IsNullOrEmpty(item.IconBase64) && _icons != null)
            {
                item.IconFile = _icons.Save(item.IconBase64) ?? item.IconFile;
            }
            item.SetIconPath(_icons?.PathOf(item.IconFile) ?? "");

            // 历史记录始终写入，和气泡开关无关
            _store?.Add(item);

            if (_tray == null) return;
            if (IsPaused) return;         // 托盘「暂停推送」：只记录，不弹通知
            if (!_enableWindowsToast) return; // 气泡开关关闭：只记录，不弹通知

            ShowMergedToast(item);
        });
    }

    /// <summary>
    /// 连续消息合并：5 秒内到达的多条消息显示成「N 条新消息 + 最近 3 条摘要」。
    /// 单条时保持原来的即时显示，不做延迟。
    /// </summary>
    private void ShowMergedToast(MessageItem item)
    {
        var now = DateTime.Now;
        _recent.Add(item);
        _recent.RemoveAll(m => (now - m.ReceivedAt) > MergeWindow);

        if (_recent.Count == 1)
        {
            ShowToast(ToastTitle(item), ToastBody(item), item.IconPath, item.Silent);
            return;
        }

        var title = $"{_recent.Count} 条新消息";
        var lines = _recent.TakeLast(3).Select(m =>
            $"{m.Source} · {Shorten(string.IsNullOrEmpty(m.Content) ? m.Title : m.Content, 26)}");
        var body = string.Join("\n", lines);
        var silent = _recent.All(m => m.Silent);
        ShowToast(title, body, _recent[^1].IconPath, silent);
    }

    private void ShowToast(string title, string body, string? iconPath, bool silent)
    {
        // Toast 失败时退回托盘气泡，保证消息一定能被看到
        if (_toast == null || !_toast.Show(title, body, iconPath, silent))
            _tray?.ShowBalloon(title, body, System.Windows.Forms.ToolTipIcon.Info);
    }

    private static string ToastTitle(MessageItem item)
        => string.IsNullOrEmpty(item.Title) ? item.Source : $"{item.Source} · {item.Title}";

    private static string ToastBody(MessageItem item)
        => string.IsNullOrEmpty(item.Content) ? "(无内容)" : item.Content;

    private static string Shorten(string s, int max)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _pauseTimer?.Stop();
            _store?.Flush();
            _snapshotWindow?.Close();
            _tray?.Dispose();
            _server?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog(ex);
        }
        base.OnExit(e);
    }

    /// <summary>把未处理异常写进 logs/crash.log，方便事后定位。</summary>
    private static void CrashLog(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MessageBridge", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不再抛出
        }
    }
}
