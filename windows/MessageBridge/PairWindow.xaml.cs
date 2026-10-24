using System.Media;
using System.Windows;
using MessageBridge.Core;

namespace MessageBridge;

public partial class PairWindow : Window
{
    private PendingPair? _current;
    /// <summary>用户点击「拒绝」时的回调。</summary>
    public Action? Rejected { get; set; }

    public PairWindow()
    {
        InitializeComponent();
    }

    public void ShowPair(PendingPair pending)
    {
        _current = pending;
        DeviceText.Text = $"来自：{pending.DeviceName}\nIP：{pending.RemoteIp}";
        PinText.Text = pending.Pin;
        Show();
        Activate();
        SystemSounds.Asterisk.Play();
    }

    private void OnRejectClicked(object sender, RoutedEventArgs e)
    {
        Rejected?.Invoke();
        Hide();
    }
}
