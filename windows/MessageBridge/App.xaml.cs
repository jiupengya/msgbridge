using System.IO;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using MessageBridge.Core;
using MessageBridge.Services;

namespace MessageBridge;

public partial class App : Application
{
    /// <summary>
    /// UI 线程调度器。WinUI 3 没有 WPF 的 Dispatcher，后台线程回调统一走这里。
    /// </summary>
    public static DispatcherQueue Ui { get; private set; } = null!;

    /// <summary>单实例互斥体名字。带 .WinUI 后缀，和旧版 WPF 构建的实例互不干扰。</summary>
    private const string MutexName = "MessageBridge.SingleInstance.WinUI";

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
    private PairWindow? _pairWindow;
    private string _dataDir = "";
    private string _configPath = "";

    /// <summary>连续通知合并窗口：这段时间内到达的消息合并成「N 条新消息」。</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(5);
    private readonly List<MessageItem> _recent = new();

    /// <summary>暂停推送的截止时间（null 表示未暂停）。</summary>
    private DateTime? _pauseUntil;
    private DispatcherQueueTimer? _pauseTimer;

    // ---- 两个独立开关的状态 ----

    /// <summary>电脑是否弹出系统 Toast（持久化到 config.json，两端都能改）。</summary>
    private bool _enableWindowsToast = true;

    /// <summary>手机当前是否处于勿扰（由手机上报）。</summary>
    private bool _dndOn;

    /// <summary>手机是否授予了修改勿扰的权限；false 时电脑端勿扰开关置灰。</summary>
    private bool _canSetDnd;

    /// <summary>手机是否已配对连接（断连时快照清空、远程开关置灰）。</summary>
    private bool _phoneConnected;

    /// <summary>手机从什么时候开始连不上的，用来判断「持续断开 30 秒」。</summary>
    private DateTime? _phoneDisconnectedSince;

    /// <summary>上次自动连 WiFi 的时间与累计次数，避免反复触发。</summary>
    private DateTime _lastAutoWifiAttempt = DateTime.MinValue;
    private int _autoWifiAttempts;
    private DispatcherQueueTimer? _autoWifiTimer;

    private sealed class Config
    {
        public int Port { get; set; } = 17001;

        /// <summary>是否弹出 Windows 系统通知气泡。</summary>
        public bool EnableWindowsToast { get; set; } = true;

        /// <summary>
        /// 自动 WiFi 兜底只在「这块以太网」上启用，用网关 MAC 识别
        /// （默认是家里那台 Redmi 路由器的 LAN 口）。
        /// </summary>
        public string TargetGatewayMac { get; set; } = "88-C3-97-44-40-95";

        /// <summary>手机连不上时，是否自动经蓝牙让手机连回 WiFi。</summary>
        public bool AutoWifiFallback { get; set; } = true;

        /// <summary>
        /// 已配对手机的**经典蓝牙 MAC**（配对后固定不变）。
        /// 电脑靠它直接连手机的 SPP 端口，不需要广播发现。
        /// </summary>
        public string PhoneBluetoothMac { get; set; } = "68:C4:4C:3F:B3:D3";
    }

    private Config _config = new();

    /// <summary>蓝牙带外通道（懒创建，第一次用才连设备）。</summary>
    private BleClient? _bleClient;
    private SppClient? _sppClient;
    private bool _bleBusy;

    /// <summary>把 "68:C4:4C:3F:B3:D3" 这种 MAC 文本转成 BluetoothAddress。</summary>
    private static ulong ParseMac(string mac)
    {
        var hex = mac.Replace(":", "").Replace("-", "").Trim();
        return ulong.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? v
            : 0UL;
    }

    private SppClient GetSppClient()
    {
        if (_sppClient == null)
        {
            _sppClient = new SppClient();
            _sppClient.Log += LogAutoWifi;
        }
        return _sppClient;
    }

    private static readonly JsonSerializerOptions ConfigJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private bool IsPaused => _pauseUntil.HasValue && DateTime.Now < _pauseUntil.Value;

    /// <summary>
    /// 诊断入口：`MessageBridge.exe --spp-test` 走经典蓝牙 RFCOMM 连接手机，
    /// 发一次 ping 并读回包，结果写进 spp-test.log。
    /// </summary>
    private async void RunSppSelfTest()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            // 手机经典蓝牙 MAC（配对后固定）
            const ulong address = 0x68C44C3FB3D3;
            var device = await Windows.Devices.Bluetooth.BluetoothDevice
                .FromBluetoothAddressAsync(address);
            log.AppendLine($"device: {device?.Name} / {device?.ConnectionStatus}");

            if (device != null)
            {
                var id = Windows.Devices.Bluetooth.Rfcomm.RfcommServiceId.FromUuid(
                    Guid.Parse("00001101-0000-1000-8000-00805F9B34FB"));
                var services = await device.GetRfcommServicesForIdAsync(
                    id, Windows.Devices.Bluetooth.BluetoothCacheMode.Uncached);
                log.AppendLine($"services: {services.Error} count={services.Services.Count}");

                if (services.Services.Count > 0)
                {
                    var service = services.Services[0];
                    log.AppendLine(
                        $"host={service.ConnectionHostName} name={service.ConnectionServiceName}");

                    // 用 StreamSocket 连 RFCOMM 服务名，比自己开 socket 更省事
                    var socket = new Windows.Networking.Sockets.StreamSocket();
                    try
                    {
                        await socket.ConnectAsync(
                            service.ConnectionHostName, service.ConnectionServiceName);
                        log.AppendLine("socket connected");

                        var writer = new Windows.Storage.Streams.DataWriter(socket.OutputStream);
                        writer.WriteString("ping\n");
                        await writer.StoreAsync();
                        writer.DetachStream();

                        var reader = new Windows.Storage.Streams.DataReader(socket.InputStream)
                        {
                            InputStreamOptions =
                                Windows.Storage.Streams.InputStreamOptions.Partial
                        };
                        var read = await reader.LoadAsync(64);
                        var text = read > 0 ? reader.ReadString(read) : "";
                        log.AppendLine($"reply: {text.Trim()}");
                    }
                    finally
                    {
                        socket.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("error: " + ex);
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "spp-test.log"), log.ToString());
        }
        catch
        {
            // 忽略
        }

        Exit();
    }

    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 诊断入口：`MessageBridge.exe --ble-test` 只跑一次蓝牙通道并把过程写进
    /// ble-test.log，然后退出。用来在没人点托盘的时候验证 BLE 链路是否通。
    /// </summary>
    private async void RunBleSelfTest()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            log.AppendLine($"ethernet: {NetworkInfo.IsOnEthernet()} / {NetworkInfo.DescribeEthernet()}");
            using var ble = new BleClient();
            ble.Log += line => log.AppendLine(line);
            log.AppendLine("scan+connect+ping ...");
            log.AppendLine("ping: " + await ble.PingAsync(TimeSpan.FromSeconds(20)));
            log.AppendLine("requesting wifi ...");
            var reply = await ble.RequestWifiAsync(TimeSpan.FromSeconds(45));
            log.AppendLine("wifi reply: " + reply);
        }
        catch (Exception ex)
        {
            log.AppendLine("error: " + ex);
        }

        try
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ble-test.log"), log.ToString());
        }
        catch
        {
            // 忽略
        }

        Exit();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Ui = DispatcherQueue.GetForCurrentThread();

        if (Environment.GetCommandLineArgs().Contains("--ble-test"))
        {
            RunBleSelfTest();
            return;
        }

        if (Environment.GetCommandLineArgs().Contains("--spp-test"))
        {
            RunSppSelfTest();
            return;
        }

        // 兜底：任何未处理异常都写一份日志，避免以后只有系统弹窗、没有线索
        UnhandledException += (_, e) =>
        {
            CrashLog(e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog(e.Exception);
            e.SetObserved();
        };

        _singleInstance = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 名字已存在不等于「有第二个实例在跑」：上一个实例崩溃时可能留下没释放的句柄。
            // 能立刻抢到所有权，就说明其实没人在跑，继续启动自己。
            bool owned;
            try
            {
                owned = _singleInstance.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // 上一个实例没正常释放，所有权已经转给我们
            }

            if (!owned)
            {
                // 静默启动：重复启动不再弹「已在运行」对话框，直接把已在运行的实例叫出来
                ShowRunningInstance();
                Exit();
                return;
            }
        }

        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MessageBridge");
        Directory.CreateDirectory(_dataDir);
        _configPath = Path.Combine(_dataDir, "config.json");
        LoadConfig();

        _icons = new IconStore(_dataDir);
        _store = new MessageStore(_dataDir, _icons);
        LoadAppInfoCache();
        _snapshot = new SnapshotStore();
        // 快照面板引用的图标不属于历史记录，裁剪时要一起保留
        _store.ExtraIconKeep = () => _snapshot.IconFiles;
        _trust = new TrustStore(_dataDir);
        AumidShortcut.EnsureRegistered(); // 系统 Toast 需要 AUMID 身份（开始菜单快捷方式）
        _toast = new ToastBridge(_dataDir);
        // 点 Windows 系统通知（Toast）：和点主窗口列表里那条一样，让手机打开对应会话
        _toast.OpenRequested += OnOpenOnPhone;
        // 横幅上的两个快捷按钮：手机勿扰 / 暂停推送 30 分钟
        _toast.DndToggleRequested += () => Ui.TryEnqueue(OnToastDndToggle);
        _toast.PauseToggleRequested += () => Ui.TryEnqueue(OnPauseToggle);
        _server = new BridgeServer { Port = _config.Port };
        _router = new MessageRouter(_server, _trust);

        _router.PairRequested += OnPairRequested;
        _router.PairResult += OnPairResult;
        _router.MessageReceived += OnMessageReceived;
        _router.NotificationChanged += OnNotificationChanged;
        _router.InterruptionFilterChanged += OnInterruptionFilterChanged;
        _router.WindowsToastRequested += OnWindowsToastRequested;

        _tray = new Tray();
        // 托盘事件在托盘自己的 STA 线程上触发，统一转回 UI 线程再动窗口
        _tray.OpenRequested += () => Ui.TryEnqueue(() => ShowMainWindow(live: false));
        // 左键是开关：面板已经显示时再点一次就收起
        _tray.SnapshotRequested += () => Ui.TryEnqueue(() => ShowMainWindow(live: true));
        _tray.ExitRequested += () => Ui.TryEnqueue(OnTrayExit);
        _tray.PauseToggleRequested += () => Ui.TryEnqueue(OnPauseToggle);
        _tray.StartupToggleRequested += () => Ui.TryEnqueue(OnStartupToggle);
        _tray.WifiConnectRequested += () => Ui.TryEnqueue(() => _ = OnWifiConnectAsync());
        _tray.AutoWifiToggleRequested += () => Ui.TryEnqueue(OnAutoWifiToggle);
        _tray.SetStartupState(StartupRegistry.IsEnabled());
        _tray.SetPauseState(false, "暂停推送 30 分钟");
        _tray.SetAutoWifiState(_config.AutoWifiFallback);

        _server.Disconnected += OnServerDisconnected;
        _server.Connected += OnServerConnected;

        // 自动 WiFi 兜底：每 2 秒看一次条件够不够
        _autoWifiTimer = Ui.CreateTimer();
        _autoWifiTimer.Interval = TimeSpan.FromSeconds(2);
        _autoWifiTimer.Tick += (_, _) => CheckAutoWifi();
        _autoWifiTimer.Start();

        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(
                $"端口 {_config.Port} 监听失败：{ex.Message}\n请检查端口是否被占用。",
                "Message", System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
            Cleanup();
            Exit();
            return;
        }

        _mainWindow = new MainWindow(_store, _snapshot!, _config.Port, _server);
        // 实时页的两个开关 + 清空手机通知，统一走 App 里的状态与路由
        _mainWindow.ToastSwitchToggled += SetWindowsToast;
        _mainWindow.InterruptionFilterToggled += OnLocalDndToggle;
        _mainWindow.ClearPhoneNotificationsRequested += OnClearPhoneNotifications;
        _mainWindow.OpenOnPhoneRequested += OnOpenOnPhone;

        // 静默启动：默认不显示主窗口，只常驻托盘。
        // 想看通知/历史从托盘左键打开。
        ApplySilentStartup();

        // 调试用：带 --live 启动时自动打开窗口并落在「实时通知」页
        // （托盘图标经常被收进溢出区，自动化测试点不到）
        if (Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase)))
        {
            var timer = Ui.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1500);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                ShowMainWindow(live: true);
            };
            timer.Start();
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
    /// 静默启动：窗口照常创建（消息照收照记），只是不显示。
    /// WinUI 3 的窗口要 Activate() 才显示，这里干脆不调用它。
    /// </summary>
    private void ApplySilentStartup()
    {
        _mainWindow?.AppWindow.Hide();
    }

    /// <summary>重复启动时唤醒已运行的实例（主窗口最小化操作固定在主窗口里处理）。</summary>
    private static void ShowRunningInstance()
    {
        const string slotName = "MessageBridge.MainWindow.Activate.Slot.WinUI";

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

    /// <param name="live">true 时打开并落在「实时通知」页，false 时落在「历史记录」页。</param>
    private void ShowMainWindow(bool live = false)
    {
        _mainWindow?.ShowFromTray(live);
    }

    private void OnTrayExit()
    {
        // 资源统一在 Cleanup 里释放：之前两边各 Dispose 一次，
        // 第二次对已释放的 CancellationTokenSource 调 Cancel() 会直接崩掉进程。
        Cleanup();
        Exit();
    }

    // ---- 实时通知（主窗口里的那一页） ----

    private void RefreshLiveState()
    {
        _mainWindow?.SetLiveState(
            _phoneConnected,
            _enableWindowsToast,
            _dndOn,
            _canSetDnd);
    }

    /// <summary>
    /// 包名 → (应用显示名, 图标文件名)。
    /// 连接后的批量补发里，手机端偶尔只能给出包名、也不带图标（挂久了的通知取不到 Icon），
    /// 这里用之前正常推送时记下的信息补全，让快照面板和主界面看起来一致。
    /// 启动时先从历史记录里重建一份，所以重启后照样有效。
    /// </summary>
    private readonly Dictionary<string, (string Name, string IconFile)> _appInfo =
        new(StringComparer.Ordinal);

    private void LoadAppInfoCache()
    {
        if (_store == null) return;
        foreach (var m in _store.Messages)
        {
            if (string.IsNullOrEmpty(m.PackageName) || string.IsNullOrEmpty(m.IconFile)) continue;
            _appInfo[m.PackageName] = (m.Source, m.IconFile);
        }
    }

    private void OnNotificationChanged(NotificationUpdate update)
    {
        Ui.TryEnqueue(() =>
        {
            if (_snapshot == null || _icons == null) return;

            string? fallbackIcon = null;
            if (!string.IsNullOrEmpty(update.PackageName))
            {
                if (!update.IsSync)
                {
                    // 正常推送：顺手更新缓存
                    var file = _icons.Save(update.IconBase64);
                    if (!string.IsNullOrEmpty(update.Source))
                    {
                        if (!string.IsNullOrEmpty(file))
                            _appInfo[update.PackageName] = (update.Source, file);
                        else if (_appInfo.TryGetValue(update.PackageName, out var old))
                            _appInfo[update.PackageName] = (update.Source, old.IconFile);
                    }
                }
                else if (_appInfo.TryGetValue(update.PackageName, out var info))
                {
                    // 补发：名称退化成包名时换成应用名，图标缺失时用缓存图标
                    if (string.IsNullOrEmpty(update.Source) || update.Source == update.PackageName)
                        update.Source = info.Name;
                    fallbackIcon = info.IconFile;
                }
            }

            if (update.IsCancel)
                _snapshot.Remove(update.Key);
            else
                _snapshot.Upsert(update, _icons, fallbackIcon);
        });
    }

    private void OnInterruptionFilterChanged(int filter, bool canSet)
    {
        Ui.TryEnqueue(() =>
        {
            _dndOn = filter == 0;
            _canSetDnd = canSet;
            RefreshLiveState();
        });
    }

    /// <summary>手机端拨动了 Windows 气泡开关。</summary>
    private void OnWindowsToastRequested(bool enable)
    {
        Ui.TryEnqueue(() => SetWindowsToast(enable));
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
        RefreshLiveState();
    }

    /// <summary>面板上拨动了「手机勿扰」开关：下发指令，等手机上报真实状态。</summary>
    private void OnLocalDndToggle(int filter)
    {
        if (_router?.SendSetInterruptionFilter(filter) != true) return;
        // 乐观更新，手机成功后会回 state.interruptionFilter 纠正
        _dndOn = filter == 0;
        RefreshLiveState();
    }

    /// <summary>横幅上的「开启/关闭勿扰」按钮：切换手机勿扰。</summary>
    private void OnToastDndToggle()
    {
        OnLocalDndToggle(_dndOn ? 2 : 0);
    }

    /// <summary>面板上点了「清空手机通知」。</summary>
    private void OnClearPhoneNotifications()
    {
        _router?.SendClearNotifications();
    }

    /// <summary>
    /// 电脑上点了某条通知：让手机去打开。
    /// 手机端优先模拟点击那条通知（跳到对应会话），通知没了就退化成打开应用。
    /// </summary>
    private void OnOpenOnPhone(OpenRequest request)
    {
        if (_router?.SendOpenNotification(request.Key, request.PackageName) != true)
        {
            Ui.TryEnqueue(() => _mainWindow?.ShowHint("手机未连接，无法打开", error: true));
            return;
        }

        var name = string.IsNullOrEmpty(request.DisplayName) ? "应用" : request.DisplayName;
        Ui.TryEnqueue(() => _mainWindow?.ShowHint($"已让手机打开 {name}"));
    }

    private void OnServerDisconnected(BridgeConnection conn)
    {
        Ui.TryEnqueue(() =>
        {
            _phoneConnected = false;
            _phoneDisconnectedSince = DateTime.Now;
            _canSetDnd = false;
            _dndOn = false;
            _snapshot?.Clear(); // 断连即销毁快照，重连时手机重新批量补发
            RefreshLiveState();
        });
    }

    /// <summary>
    /// 手机连上来（包括「已配对设备直接重连」，这条路径以前不会触发 PairResult）。
    /// 之前只在配对成功时把 _phoneConnected 置 true，重连后一直是 false，
    /// 导致快照面板永远显示「未连接」、勿扰开关永远置灰。
    /// </summary>
    private void OnServerConnected(BridgeConnection conn)
    {
        Ui.TryEnqueue(() =>
        {
            _phoneConnected = true;
            _phoneDisconnectedSince = null;
            // 连接建立后把 Windows 气泡开关状态同步给手机
            _router?.SendWindowsToastState(_enableWindowsToast);
            RefreshLiveState();
            // 手机连上电脑就弹这一句，不带任何状态文字
            _toast?.ShowRaw("Ciallo～(∠・ω< )⌒★");
        });
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
                _pauseTimer = Ui.CreateTimer();
                _pauseTimer.Interval = TimeSpan.FromSeconds(20);
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

    // ---- 蓝牙兜底：让手机连上 WiFi ----

    /// <summary>托盘「自动连回 WiFi」勾选项。</summary>
    private void OnAutoWifiToggle()
    {
        _config.AutoWifiFallback = !_config.AutoWifiFallback;
        SaveConfig();
        _tray?.SetAutoWifiState(_config.AutoWifiFallback);
    }

    /// <summary>
    /// 自动兜底的条件检查：连着目标以太网 + 手机断开 30 秒以上 + 不在冷却期。
    /// 蓝牙能不能扫到手机留到真正执行时判断，扫不到就静默跳过。
    /// </summary>
    private void CheckAutoWifi()
    {
        if (!_config.AutoWifiFallback) return;
        if (_bleBusy) return;

        // 6 秒内有包就算活着（手机每 3 秒发一次 ping）
        if (PhoneReachable()) return;

        // 连接还挂着但已经收不到包 → 假活。主动断开，界面才会立刻变成「未连接」。
        if (_phoneConnected)
        {
            LogAutoWifi("连接假活，主动断开");
            _phoneConnected = false;
            _phoneDisconnectedSince = _router?.LastActivity ?? DateTime.Now;
            _server?.DisconnectAll();
            RefreshLiveState();
        }

        var deadSince = _phoneDisconnectedSince ?? DateTime.Now;
        _phoneDisconnectedSince = deadSince;
        if ((DateTime.Now - deadSince).TotalSeconds < 8) return;
        if ((DateTime.Now - _lastAutoWifiAttempt).TotalMinutes < 5) return;
        if (_autoWifiAttempts >= 20) return;
        if (!NetworkInfo.IsTargetEthernet(_config.TargetGatewayMac)) return;

        _lastAutoWifiAttempt = DateTime.Now;
        _autoWifiAttempts++;
        _ = RunAutoWifiAsync();
    }

    /// <summary>
    /// 手机是否真的在线：不但要连着，还得 6 秒内收到过包。
    /// 关 WiFi 时 TCP 不会立刻断开（没有 FIN），只看「连接存在」会被骗。
    /// </summary>
    private bool PhoneReachable()
    {
        if (!_phoneConnected) return false;
        var last = _router?.LastActivity ?? DateTime.MinValue;
        return (DateTime.Now - last).TotalSeconds <= 6;
    }

    /// <summary>
    /// 自动执行：先确认蓝牙能扫到手机，再弹「Ciallo」提示，最后下发指令。
    /// 扫不到手机（没开蓝牙 / 推送服务没跑）就什么也不做，不打扰用户。
    /// </summary>
    private async Task RunAutoWifiAsync()
    {
        _bleBusy = true;
        try
        {
            var address = ParseMac(_config.PhoneBluetoothMac);
            // 不弹电脑通知：这条链路是「悄悄把网修好」，只在日志里留痕
            var reply = await GetSppClient().RequestWifiAsync(address, TimeSpan.FromSeconds(30));
            if (reply == null)
            {
                LogAutoWifi("SPP 连不上手机，跳过");
                return;
            }
            LogAutoWifi($"手机回复：{reply}");
        }
        catch (Exception ex)
        {
            LogAutoWifi($"失败：{ex.Message}");
        }
        finally
        {
            _bleBusy = false;
        }
    }

    private void LogAutoWifi(string message)
    {
        try
        {
            var dir = Path.Combine(_dataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "auto-wifi.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 托盘「让手机连上 WiFi」：走 BLE 把指令送到手机，手机用 Shizuku 的 shell
    /// 身份执行 `cmd wifi`。只在电脑接着有线以太网时可用。
    /// </summary>
    private async Task OnWifiConnectAsync()
    {
        if (_bleBusy) return;
        if (!NetworkInfo.IsOnEthernet())
        {
            _tray?.ShowBalloon("手机 WiFi", "电脑当前没有插有线以太网，不启用蓝牙兜底。");
            return;
        }

        _bleBusy = true;
        _tray?.SetWifiBusy(true);
        try
        {
            var address = ParseMac(_config.PhoneBluetoothMac);
            var reply = await GetSppClient().RequestWifiAsync(address, TimeSpan.FromSeconds(30));
            if (reply == null)
            {
                _tray?.ShowBalloon("手机 WiFi", "蓝牙连不上手机，确认手机开着蓝牙且推送服务在运行。");
                return;
            }

            if (reply.StartsWith("ok:", StringComparison.Ordinal))
            {
                var ssid = reply[3..];
                _tray?.ShowBalloon("手机 WiFi", $"手机已连上 {ssid}。");
            }
            else
            {
                _tray?.ShowBalloon("手机 WiFi", $"没成功：{reply.TrimStart('e', 'r', 'r', ':')}");
            }
        }
        catch (Exception ex)
        {
            CrashLog(ex);
            _tray?.ShowBalloon("手机 WiFi", $"蓝牙通道失败：{ex.Message}");
        }
        finally
        {
            _bleBusy = false;
            _tray?.SetWifiBusy(false);
        }
    }

    // ---- 配对 ----

    private void OnPairRequested(PendingPair pending)
    {
        Ui.TryEnqueue(() =>
        {
            _pairWindow ??= new PairWindow { Rejected = () => _router?.CancelPairing(pending.DeviceId) };
            _pairWindow.ShowPair(pending);
        });
    }

    private void OnPairResult(BridgeConnection conn, bool ok)
    {
        Ui.TryEnqueue(() =>
        {
            _pairWindow?.HidePair();
            _mainWindow?.UpdateStatus();

            if (ok)
            {
                _phoneConnected = true;
                // 连接建立后把 Windows 气泡开关状态同步给手机
                _router?.SendWindowsToastState(_enableWindowsToast);
                RefreshLiveState();
            }
            else
            {
                ShowToast(
                    "Message · 配对",
                    $"与 {conn.DeviceName} 配对失败（PIN 不正确或已过期）",
                    null, false);
            }
        });
    }

    // ---- 消息 ----

    private void OnMessageReceived(MessageItem item)
    {
        Ui.TryEnqueue(() =>
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
            ShowToast(ToastTitle(item), ToastBody(item), item.IconPath, item.Silent,
                OpenFor(item), _dndOn);
            return;
        }

        var title = $"{_recent.Count} 条新消息";
        var lines = _recent.TakeLast(3).Select(m =>
            $"{m.Source} · {Shorten(string.IsNullOrEmpty(m.Content) ? m.Title : m.Content, 26)}");
        var body = string.Join("\n", lines);
        var silent = _recent.All(m => m.Silent);
        // 合并提示点开后打开「最新那条」，和通知正文默认展示的一致
        ShowToast(title, body, _recent[^1].IconPath, silent, OpenFor(_recent[^1]), _dndOn);
    }

    /// <summary>Toast / 列表点击时要打开的目标：key 精确到会话，包名兜底。</summary>
    private static OpenRequest OpenFor(MessageItem item)
        => new(item.Key, item.PackageName, item.Source);

    /// <param name="dndOn">手机当前是否勿扰，决定横幅上按钮文字是「开启勿扰」还是「关闭勿扰」。</param>
    private void ShowToast(string title, string body, string? iconPath, bool silent,
        OpenRequest? open = null, bool? dndOn = null)
    {
        // Toast 失败时退回托盘气泡，保证消息一定能被看到
        if (_toast == null || !_toast.Show(title, body, iconPath, silent, open, dndOn))
            _tray?.ShowBalloon(title, body);
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

    private bool _cleanedUp;

    /// <summary>退出前统一释放（对应 WPF 版的 OnExit）。</summary>
    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try
        {
            _pauseTimer?.Stop();
            _store?.Flush();
            _pairWindow?.Close();
            _mainWindow?.Close();
            _tray?.Dispose();
            _server?.Dispose();
            _bleClient?.Dispose();
            _sppClient?.Dispose();
        }
        catch (Exception ex)
        {
            CrashLog(ex);
        }
    }

    /// <summary>把未处理异常写进 logs/crash.log，方便事后定位。</summary>
    private static void CrashLog(Exception? ex)    {
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
