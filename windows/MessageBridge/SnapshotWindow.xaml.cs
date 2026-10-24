using System.Windows;
using System.Windows.Threading;
using MessageBridge.Core;

namespace MessageBridge;

/// <summary>
/// 托盘左键弹出的「实时通知快照面板」。
/// 独立于 messages.json 历史：这里只显示手机此刻还挂着的通知，断连即清空。
/// 置顶 + 无边框，点击窗口外部（失去激活）自动收起。
/// </summary>
public partial class SnapshotWindow : Window
{
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

    public SnapshotWindow(SnapshotStore store)
    {
        InitializeComponent();
        SnapshotList.ItemsSource = store.Items;

        store.Changed += UpdateEmptyState;
        UpdateEmptyState();
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
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 12;
        Top = wa.Bottom - Height - 12;

        Show();
        Activate();

        // 显示后短暂窗口期内不自动收起，之后才允许「点击外部关闭」
        _allowAutoHide = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _allowAutoHide = true;
        };
        timer.Start();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        if (_allowAutoHide) Hide();
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
}
