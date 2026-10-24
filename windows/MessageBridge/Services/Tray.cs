using System.IO;
using System.Threading;
using Microsoft.Win32;
using System.Windows.Forms;

namespace MessageBridge.Services;

/// <summary>
/// 系统托盘图标（WinUI 3 没有托盘 API，这里用 WinForms 的 NotifyIcon，
/// 跑在单独的 STA 线程上，自带消息循环）。
///
/// 图标是纯色线条风格：浅色任务栏用深色线条、深色任务栏用浅色线条，
/// 跟随系统主题自动切换（见 tools/make_tray_icons.py）。
/// </summary>
public sealed class Tray : IDisposable
{
    private static readonly string DarkIconPath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "tray_dark.ico");
    private static readonly string LightIconPath =
        Path.Combine(AppContext.BaseDirectory, "Resources", "tray_light.ico");

    private readonly Thread _thread;
    private SynchronizationContext? _ctx;
    private NotifyIcon? _icon;
    private ToolStripMenuItem? _pauseItem;
    private ToolStripMenuItem? _startupItem;
    private bool _lightTaskbar = true;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    /// <summary>左键单击：弹出实时通知快照面板。</summary>
    public event Action? SnapshotRequested;
    /// <summary>点击「暂停推送 / 恢复推送」。</summary>
    public event Action? PauseToggleRequested;
    /// <summary>点击「开机自启」。</summary>
    public event Action? StartupToggleRequested;

    /// <summary>左键单击后要等过双击窗口期，避免双击时也顺带弹出快照面板。</summary>
    private bool _singleClickPending;

    public Tray()
    {
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                // 手动装 WinForms 的同步上下文：先装好，之后外部线程才能把调用投递进来
                var ctx = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(ctx);
                _ctx = ctx;

                BuildTray();
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            }
            catch
            {
                // 托盘创建失败不阻塞主流程
            }
            finally
            {
                ready.Set();
            }

            Application.Run(new ApplicationContext());
        })
        {
            IsBackground = true,
            Name = "MessageBridge.Tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>必须在托盘线程上调用。</summary>
    private void BuildTray()
    {
        _lightTaskbar = IsLightTaskbar();

        var icon = LoadIcon();
        _icon = new NotifyIcon
        {
            Icon = icon,
            Text = "Message · 手机消息推送",
            Visible = true
        };
        Log($"tray created: lightTaskbar={_lightTaskbar} icon={icon.Width}x{icon.Height} " +
            $"path={(_lightTaskbar ? DarkIconPath : LightIconPath)}");

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
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();

        // 左键单击 -> 快照面板；双击 -> 主窗口。
        // NotifyIcon 双击时也会先触发 MouseClick，所以单击要等过双击窗口期才生效。
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            _singleClickPending = true;
            var timer = new System.Windows.Forms.Timer
            {
                Interval = SystemInformation.DoubleClickTime
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                timer.Dispose();
                if (!_singleClickPending) return;
                _singleClickPending = false;
                SnapshotRequested?.Invoke();
            };
            timer.Start();
        };

        _icon.DoubleClick += (_, _) =>
        {
            _singleClickPending = false;
            OpenRequested?.Invoke();
        };
    }

    /// <summary>当前任务栏是浅色吗（浅色任务栏要用深色图标才看得见）。</summary>
    private static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch
        {
            return true; // 读不到就按浅色任务栏处理（Windows 默认）
        }
    }

    private System.Drawing.Icon LoadIcon()
    {
        var path = _lightTaskbar ? DarkIconPath : LightIconPath;
        try
        {
            if (File.Exists(path))
                return new System.Drawing.Icon(path, SystemInformation.SmallIconSize);
        }
        catch
        {
            // 落到备用图标
        }

        // 兜底：用 exe 自带图标，保证托盘至少有一个图标
        try
        {
            var fallback = Path.Combine(AppContext.BaseDirectory, "Resources", "msgicon.ico");
            if (File.Exists(fallback))
                return new System.Drawing.Icon(fallback, SystemInformation.SmallIconSize);
        }
        catch
        {
            // 忽略
        }

        return System.Drawing.SystemIcons.Application;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed) return;
        Run(() =>
        {
            var light = IsLightTaskbar();
            if (light == _lightTaskbar || _icon == null) return;
            _lightTaskbar = light;
            var old = _icon.Icon;
            _icon.Icon = LoadIcon();
            old?.Dispose();
        });
    }

    /// <summary>把动作投递到托盘线程执行（NotifyIcon 有线程亲和性）。</summary>
    private void Run(Action action)
    {
        var ctx = _ctx;
        if (ctx == null || _disposed) return;
        try
        {
            ctx.Post(_ =>
            {
                if (_disposed) return;
                action();
            }, null);
        }
        catch
        {
            // 退出阶段忽略
        }
    }

    /// <summary>更新「暂停推送」菜单项的文字与勾选状态。</summary>
    public void SetPauseState(bool paused, string text)
        => Run(() =>
        {
            if (_pauseItem == null) return;
            _pauseItem.Text = text;
            _pauseItem.Checked = paused;
        });

    /// <summary>更新「开机自启」勾选状态。</summary>
    public void SetStartupState(bool enabled)
        => Run(() =>
        {
            if (_startupItem == null) return;
            _startupItem.Checked = enabled;
        });

    public void ShowBalloon(string title, string text)
        => Run(() =>
        {
            if (_icon == null) return;
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = text;
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(5000);
        });

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch
        {
            // 忽略
        }

        try
        {
            Run(() =>
            {
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                    _icon = null;
                }
                Application.ExitThread();
            });
        }
        catch
        {
            // 退出流程中重复释放不应抛给上层
        }
    }

    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MessageBridge", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "tray.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }
}
