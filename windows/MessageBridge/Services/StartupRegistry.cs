using Microsoft.Win32;

namespace MessageBridge.Services;

/// <summary>
/// 开机自启：写 HKCU 的 Run 项，不需要管理员权限。
/// </summary>
public static class StartupRegistry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Message";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && !string.IsNullOrEmpty(value);
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表不可写时忽略，不影响程序运行
        }
    }
}
