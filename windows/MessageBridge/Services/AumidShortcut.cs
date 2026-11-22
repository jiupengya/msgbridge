using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace MessageBridge.Services;

/// <summary>
/// 为当前 exe 注册 AppUserModelID（开始菜单快捷方式 + 注册表项）。
/// Windows Toast 通知要求应用有 AUMID 身份，否则 CreateToastNotifier(aumid).Show() 会失败。
/// 每次启动都校验：快捷方式指向旧构建目录或缺少 AUMID 属性时会重建，
/// 避免之前版本留下的无效快捷方式导致通知永久失效。
/// </summary>
public static class AumidShortcut
{
    public const string AppId = "MessageBridge.App";
    public const string DisplayName = "Message";

    /// <summary>
    /// 以 MSIX 包身份运行时，AUMID 由系统按「包族名!应用 Id」生成，
    /// 这时不需要也不该再去伪造快捷方式。
    /// </summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>Toast 要用的 AUMID：打包时取包的，绿色版时取自己注册的。</summary>
    public static string Aumid => IsPackaged
        ? $"{Windows.ApplicationModel.Package.Current.Id.FamilyName}!App"
        : AppId;

    private static bool DetectPackaged()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current != null;
        }
        catch
        {
            // 未打包时访问 Package.Current 会抛异常
            return false;
        }
    }

    private const string LnkName = "Message.lnk";
    // 旧版本留下的快捷方式名，启动时清理掉，避免开始菜单出现两个同名应用
    private const string LegacyLnkName = "消息桥.lnk";
    private const ushort VtLpwstr = 31;

    private static readonly Guid AppUserModelIdFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    public static void EnsureRegistered()
    {
        try
        {
            // 打包应用自带正规身份，不需要伪造快捷方式
            if (IsPackaged) return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            RemoveLegacyShortcut();
            EnsureShortcut(exe);
            EnsureRegistryEntry(exe);
        }
        catch
        {
            // 注册失败则 Toast 可能不显示，但不影响程序其他功能
        }
    }

    /// <summary>删除旧名称的开始菜单快捷方式（名称从「消息桥」改为 Message）。</summary>
    private static void RemoveLegacyShortcut()
    {
        try
        {
            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs",
                LegacyLnkName);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch
        {
            // 删不掉不影响新快捷方式
        }
    }

    private static void EnsureShortcut(string exe)
    {
        var lnkDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        var lnkPath = Path.Combine(lnkDir, LnkName);

        if (File.Exists(lnkPath) && ShortcutIsValid(lnkPath, exe)) return;

        Directory.CreateDirectory(lnkDir);
        var link = new ShellLink();
        try
        {
            var shell = (IShellLinkW)link;
            shell.SetPath(exe);
            shell.SetDescription("Message - 手机通知推送");
            shell.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? exe);
            shell.SetIconLocation(exe, 0);

            // 先设置 AUMID 属性并提交，再把整个快捷方式写入磁盘。
            // 顺序反了（先 Save 再 Commit）会导致磁盘上的 lnk 没有 AUMID。
            var store = (IPropertyStore)link;
            var key = new PROPERTYKEY(AppUserModelIdFmtid, 5);
            var pv = new PROPVARIANT { vt = VtLpwstr };
            pv.pwszVal = Marshal.StringToCoTaskMemUni(AppId);
            try
            {
                store.SetValue(ref key, ref pv);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(pv.pwszVal);
            }

            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static bool ShortcutIsValid(string lnkPath, string exe)
    {
        object? com = null;
        try
        {
            com = new ShellLink();
            ((IPersistFile)com).Load(lnkPath, 0); // STGM_READ

            var path = new StringBuilder(1024);
            ((IShellLinkW)com).GetPath(path, path.Capacity, IntPtr.Zero, 0);
            if (!string.Equals(path.ToString(), exe, StringComparison.OrdinalIgnoreCase))
                return false;

            var store = (IPropertyStore)com;
            var key = new PROPERTYKEY(AppUserModelIdFmtid, 5);
            store.GetValue(ref key, out var pv);
            try
            {
                if (pv.vt != VtLpwstr || pv.pwszVal == IntPtr.Zero) return false;
                return string.Equals(
                    Marshal.PtrToStringUni(pv.pwszVal), AppId, StringComparison.Ordinal);
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (com != null) Marshal.FinalReleaseComObject(com);
        }
    }

    /// <summary>
    /// 注册 HKCU\Software\Classes\AppUserModelId\{AppId}，
    /// 让应用出现在「设置 → 通知」列表中，并保证通知中心能识别该身份。
    /// </summary>
    private static void EnsureRegistryEntry(string exe)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppId);
        if (key == null) return;
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("IconUri", exe, RegistryValueKind.String);
        key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(Guid f, uint p) { fmtid = f; pid = p; }
    }

    // 与原生 PROPVARIANT 同尺寸（x64 为 24 字节），避免 GetValue 写入时越界。
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }
}
