namespace MessageBridge.Core;

/// <summary>
/// 一次「在手机上打开某条通知」的请求。
/// 主窗口点列表、点 Windows 系统通知（Toast）都走这里。
/// </summary>
/// <param name="Key">手机端通知 key；历史记录 / 合并提示里没有精确 key 时为空串。</param>
/// <param name="PackageName">通知已消失时退化成按包名打开应用。</param>
/// <param name="DisplayName">只用于界面反馈（「已让手机打开 XX」）。</param>
public sealed record OpenRequest(string Key, string PackageName, string DisplayName);
