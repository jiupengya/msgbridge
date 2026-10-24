using System.Text.Json.Nodes;

namespace MessageBridge.Core;

/// <summary>
/// 消息桥协议包（参考 KDE Connect NetworkPacket）：
/// 每行一条 UTF-8 JSON，{"type":"...","id":...,"body":{...}}，以换行结尾。
/// </summary>
public sealed class Packet
{
    public static class Types
    {
        public const string PairRequest = "pair.request";
        public const string PairConfirm = "pair.confirm";
        public const string PairOk = "pair.ok";
        public const string PairReject = "pair.reject";
        public const string Notification = "notification";
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
