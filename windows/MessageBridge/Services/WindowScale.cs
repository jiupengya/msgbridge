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

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

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
