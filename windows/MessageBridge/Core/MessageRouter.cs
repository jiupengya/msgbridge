using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace MessageBridge.Core;

/// <summary>一次待确认的配对请求。</summary>
public sealed class PendingPair
{
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    public required string RemoteIp { get; init; }
    public required string Pin { get; init; }
    public DateTime ExpiresAt { get; init; } = DateTime.Now.AddMinutes(10);
}

/// <summary>
/// 消息路由：处理配对握手与业务包分发（notification / sms）。
/// 配对流程：手机发 pair.request -> 本机生成 6 位 PIN 并弹窗 -> 手机回 pair.confirm
/// -> 校验通过则记为已信任并回 pair.ok，否则回 pair.reject。
/// </summary>
public sealed class MessageRouter
{
    /// <summary>配对请求（UI 弹窗展示 PIN），后台线程回调。</summary>
    public event Action<PendingPair>? PairRequested;
    /// <summary>配对结果：成功（true）/ 失败（false）。</summary>
    public event Action<BridgeConnection, bool>? PairResult;
    /// <summary>收到一条业务消息（通知/短信）。</summary>
    public event Action<MessageItem>? MessageReceived;

    private readonly BridgeServer _server;
    private readonly TrustStore _trust;
    private readonly ConcurrentDictionary<string, PendingPair> _pendingPairs = new();

    public MessageRouter(BridgeServer server, TrustStore trust)
    {
        _server = server;
        _trust = trust;
        _server.PacketReceived += OnPacketReceived;
        _server.Disconnected += OnDisconnected;
    }

    public void CancelPairing(string deviceId)
    {
        _pendingPairs.TryRemove(deviceId, out _);
    }

    private void OnDisconnected(BridgeConnection conn)
    {
        if (conn.DeviceId != null)
        {
            // 断开不删除信任关系，也不删除 pending（PIN 有效期内重连可继续）
        }
    }

    private void OnPacketReceived(BridgeConnection conn, Packet packet)
    {
        switch (packet.Type)
        {
            case Packet.Types.PairRequest:
                HandlePairRequest(conn, packet);
                break;
            case Packet.Types.PairConfirm:
                HandlePairConfirm(conn, packet);
                break;
            case Packet.Types.Ping:
                conn.Send(Packet.Create(Packet.Types.Pong, new System.Text.Json.Nodes.JsonObject
                {
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));
                break;
            case Packet.Types.Notification:
                if (!conn.IsPaired) return; // 未配对不处理业务包
                HandleBusiness(conn, packet);
                break;
            default:
                break;
        }
    }

    private void HandlePairRequest(BridgeConnection conn, Packet packet)
    {
        var body = packet.Body;
        var deviceId = body["deviceId"]?.GetValue<string>() ?? "";
        var deviceName = body["deviceName"]?.GetValue<string>() ?? conn.RemoteIp;
        if (string.IsNullOrEmpty(deviceId)) return;

        conn.DeviceId = deviceId;
        conn.DeviceName = deviceName;

        if (_trust.IsTrusted(deviceId))
        {
            // 已信任设备：直接放行
            conn.IsPaired = true;
            _pendingPairs.TryRemove(deviceId, out _);
            conn.Send(Packet.Create(Packet.Types.PairOk, new System.Text.Json.Nodes.JsonObject
            {
                ["deviceId"] = deviceId,
                ["deviceName"] = Environment.MachineName
            }));
            PairResult?.Invoke(conn, true);
            return;
        }

        // 生成 6 位 PIN，弹窗提示
        var pin = GeneratePin();
        var pending = new PendingPair
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            RemoteIp = conn.RemoteIp,
            Pin = pin
        };
        _pendingPairs[deviceId] = pending;
        PairRequested?.Invoke(pending);
    }

    private void HandlePairConfirm(BridgeConnection conn, Packet packet)
    {
        var deviceId = conn.DeviceId;
        if (deviceId == null) return;

        var pin = packet.Body["pin"]?.GetValue<string>() ?? "";
        if (_pendingPairs.TryGetValue(deviceId, out var pending) &&
            pending.ExpiresAt >= DateTime.Now &&
            string.Equals(pending.Pin, pin, StringComparison.Ordinal))
        {
            _pendingPairs.TryRemove(deviceId, out _);
            _trust.Add(deviceId, pending.DeviceName);
            conn.IsPaired = true;
            conn.Send(Packet.Create(Packet.Types.PairOk, new System.Text.Json.Nodes.JsonObject
            {
                ["deviceId"] = deviceId,
                ["deviceName"] = Environment.MachineName
            }));
            PairResult?.Invoke(conn, true);
        }
        else
        {
            _pendingPairs.TryRemove(deviceId, out _);
            conn.Send(Packet.Create(Packet.Types.PairReject));
            PairResult?.Invoke(conn, false);
        }
    }

    private void HandleBusiness(BridgeConnection conn, Packet packet)
    {
        // isCancel=true 表示通知在手机端被移除，不展示为一条消息
        if (packet.Body["isCancel"]?.GetValue<bool>() == true) return;
        var item = MessageItem.FromNotification(packet.Body);
        MessageReceived?.Invoke(item);
    }

    private static string GeneratePin()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }
}
