using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace MessageBridge.Services;

/// <summary>
/// 窗口尺寸换算。
/// 设计稿用逻辑像素（DIP），Win32 用物理像素，中间差一个显示器缩放系数。
///
/// 两个坑：
/// 1. 窗口还没 Show 过时 <c>GetDpiForWindow</c> 只返回 96，所以只能在显示之后再算尺寸；
/// 2. <c>AppWindow.MoveAndResize</c> 的尺寸在这台 150% 缩放的机器上会被再除一次 scale
///    （传 585x780 实际只有 390x520），干脆直接用 <c>SetWindowPos</c> 按物理像素摆。
/// </summary>
public static class WindowScale
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    /// <summary>SW_SHOWNOACTIVATE：显示但不激活，不抢前台焦点。</summary>
    private const int SwShowNoActivate = 4;

    public static double For(Window window)
    {
        // 别用 XamlRoot.RasterizationScale：窗口刚显示时它可能返回 1.0 或 0.667 这种错值。
        // GetDpiForWindow 只要窗口显示过就是准的。
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) return dpi / 96.0;
        }
        catch (Exception ex)
        {
            Log($"For failed: {ex.GetType().Name}: {ex.Message}");
        }
        return 1.0;
    }

    public static int Px(double dip, double scale)
        => (int)Math.Round(dip * scale);

    /// <summary>
    /// 显示窗口但不抢焦点。托盘弹出的小面板必须这样显示，
    /// 否则会把全屏游戏/其他窗口的焦点抢走（切出一次很烦人）。
    /// </summary>
    public static void ShowNoActivate(Window window)
    {
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            ShowWindow(hwnd, SwShowNoActivate);
        }
        catch (Exception ex)
        {
            Log($"ShowNoActivate failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把窗口裁成圆角。
    ///
    /// WinUI 3 的无边框窗口（SetBorderAndTitleBar(false,false) 之后是 WS_POPUP）拿不到
    /// DWM 的自动圆角，XAML 里的 CornerRadius 只影响内容，窗口矩形本身还在，
    /// 圆角外露出的是窗口的默认白底 —— 这就是快照面板那圈明显的白边。
    /// 用 SetWindowRgn 直接把窗口形状改成圆角矩形，圆角外就真的不存在了。
    /// </summary>
    public static void ApplyRoundCorners(Window window, int radiusPx)
    {
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            var size = window.AppWindow.Size;
            if (size.Width <= 0 || size.Height <= 0) return;

            // 区域比窗口再内缩 1px：Win11 会给窗口边缘画一条 1px 的亮边，
            // 内缩之后连同那条亮边一起被裁掉，肉眼看就是干净的圆角。
            var rgn = CreateRoundRectRgn(1, 1, size.Width, size.Height,
                radiusPx * 2, radiusPx * 2);
            if (rgn == IntPtr.Zero) return;
            // 区域交给系统接管，不需要（也不能）自己 DeleteObject
            SetWindowRgn(hwnd, rgn, true);
        }
        catch (Exception ex)
        {
            Log($"ApplyRoundCorners failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>按物理像素摆放窗口（不做任何缩放换算）。</summary>
    public static bool SetBounds(Window window, int x, int y, int width, int height)
    {
        try
        {
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);
            return SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception ex)
        {
            Log($"SetBounds failed: {ex.GetType().Name}: {ex.Message}");
            return false;
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
            File.AppendAllText(Path.Combine(dir, "scale.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch
        {
            // 忽略
        }
    }
}
