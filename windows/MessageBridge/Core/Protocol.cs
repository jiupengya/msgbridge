using System.Text.Json.Nodes;

namespace MessageBridge.Core;

/// <summary>
/// 消息桥协议包（参考 KDE Connect NetworkPacket）：
/// 每行一条 UTF-8 JSON，{"protoVer":1,"type":"...","id":...,"body":{...}}，以换行结尾。
/// </summary>
public sealed class Packet
{
    /// <summary>协议版本，所有包都带上，方便以后平滑升级。</summary>
    public const int ProtoVer = 1;

    public static class Types
    {
        public const string PairRequest = "pair.request";
        public const string PairConfirm = "pair.confirm";
        public const string PairOk = "pair.ok";
        public const string PairReject = "pair.reject";
        public const string Notification = "notification";

        // 电脑 -> 手机：指令
        public const string CommandClearNotifications = "command.clearNotifications";
        public const string CommandSetInterruptionFilter = "command.setInterruptionFilter";
        /// <summary>双向：手机或电脑任一端修改 Windows 气泡开关。</summary>
        public const string CommandSetWindowsToast = "command.setWindowsToast";
        /// <summary>电脑 -> 手机：打开某条通知对应的界面（通知已消失时退化成打开应用）。</summary>
        public const string CommandOpenNotification = "command.openNotification";

        // 状态上报
        /// <summary>手机 -> 电脑：手机当前勿扰状态。</summary>
        public const string StateInterruptionFilter = "state.interruptionFilter";
        /// <summary>电脑 -> 手机：Windows 气泡开关状态。</summary>
        public const string StateWindowsToast = "state.windowsToast";

        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Bye = "bye";
    }

    public string Type { get; init; } = "";
    public long Id { get; init; }
    public JsonObject Body { get; init; } = new();

    public static Packet? TryParse(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node is not JsonObject obj) return null;
            var type = obj["type"]?.GetValue<string>();
            if (string.IsNullOrEmpty(type)) return null;
            var id = obj["id"]?.GetValue<long>() ?? 0L;
            var body = obj["body"] as JsonObject ?? new JsonObject();
            return new Packet { Type = type, Id = id, Body = body };
        }
        catch
        {
            return null;
        }
    }

    public string Serialize()
    {
        var obj = new JsonObject
        {
            ["protoVer"] = ProtoVer,
            ["type"] = Type,
            ["id"] = Id,
            ["body"] = Body
        };
        return obj.ToJsonString() + "\n";
    }

    public static Packet Create(string type, JsonObject? body = null)
    {
        return new Packet
        {
            Type = type,
            Id = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Body = body ?? new JsonObject()
        };
    }
}
