using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

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
/// 消息路由：处理配对握手与业务包分发。
/// 配对流程：手机发 pair.request -> 本机生成 6 位 PIN 并弹窗 -> 手机回 pair.confirm
/// -> 校验通过则记为已信任并回 pair.ok，否则回 pair.reject。
///
/// 业务包分两路：
///  - notification        -> <see cref="NotificationChanged"/>（实时快照，含取消）
///                          + <see cref="MessageReceived"/>（历史记录，取消不记）
///  - state.*             -> 手机上报的状态（勿扰、Windows 气泡开关回执）
/// </summary>
public sealed class MessageRouter
{
    /// <summary>
    /// 最后一次收到手机任何包的时间。手机每 3 秒发一次 ping；
    /// 超过 6 秒没有包，说明这条 TCP 只是「假活」（WiFi 关了但连接没收到 FIN）。
    /// </summary>
    public DateTime LastActivity { get; private set; } = DateTime.Now;

    /// <summary>配对请求（UI 弹窗展示 PIN），后台线程回调。</summary>
    public event Action<PendingPair>? PairRequested;
    /// <summary>配对结果：成功（true）/ 失败（false）。</summary>
    public event Action<BridgeConnection, bool>? PairResult;
    /// <summary>收到一条业务消息（通知/短信），用于写历史。</summary>
    public event Action<MessageItem>? MessageReceived;
    /// <summary>通知新增 / 更新 / 取消，用于实时快照面板。</summary>
    public event Action<NotificationUpdate>? NotificationChanged;
    /// <summary>手机上报勿扰状态：filter（0=勿扰，2=正常）+ 是否有权限修改。</summary>
    public event Action<int, bool>? InterruptionFilterChanged;
    /// <summary>手机修改了 Windows 气泡开关（双向同步时后台线程回调）。</summary>
    public event Action<bool>? WindowsToastRequested;

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

    // ---- 电脑 -> 手机的指令 ----

    /// <summary>当前已配对连接（没有则返回 null）。</summary>
    private BridgeConnection? PairedConnection => _server.Connections.FirstOrDefault(c => c.IsPaired);

    public bool IsConnected => PairedConnection != null;

    /// <summary>清空手机通知栏里可清除的通知（常驻通知系统不允许清）。</summary>
    public bool SendClearNotifications()
    {
        var conn = PairedConnection;
        return conn != null && conn.Send(Packet.Create(Packet.Types.CommandClearNotifications));
    }

    /// <summary>设置手机勿扰：0=开启勿扰，2=关闭勿扰。</summary>
    public bool SendSetInterruptionFilter(int filter)
    {
        var conn = PairedConnection;
        return conn != null && conn.Send(Packet.Create(
            Packet.Types.CommandSetInterruptionFilter,
            new JsonObject { ["filter"] = filter }));
    }

    /// <summary>请求修改 Windows 气泡开关（本地或手机发起，最终都会走这里）。</summary>
    public bool SendSetWindowsToast(bool enable)
    {
        var conn = PairedConnection;
        return conn != null && conn.Send(Packet.Create(
            Packet.Types.CommandSetWindowsToast,
            new JsonObject { ["enable"] = enable }));
    }

    /// <summary>下发 Windows 气泡开关状态给手机（连接建立、状态变更时调用）。</summary>
    public bool SendWindowsToastState(bool enable)
    {
        var conn = PairedConnection;
        return conn != null && conn.Send(Packet.Create(
            Packet.Types.StateWindowsToast,
            new JsonObject { ["enable"] = enable }));
    }

    /// <summary>
    /// 让手机打开某条通知：手机上还挂着就模拟点击它（跳到对应会话），
    /// 已经消失就退化成按包名打开应用。
    /// </summary>
    /// <param name="key">手机端的通知 key；历史记录里的旧消息传空串。</param>
    public bool SendOpenNotification(string key, string packageName)
    {
        var conn = PairedConnection;
        if (conn == null) return false;
        if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(packageName)) return false;

        return conn.Send(Packet.Create(
            Packet.Types.CommandOpenNotification,
            new JsonObject
            {
                ["id"] = key ?? "",
                ["packageName"] = packageName ?? ""
            }));
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
        LastActivity = DateTime.Now;
        switch (packet.Type)
        {
            case Packet.Types.PairRequest:
                HandlePairRequest(conn, packet);
                break;
            case Packet.Types.PairConfirm:
                HandlePairConfirm(conn, packet);
                break;
            case Packet.Types.Ping:
                conn.Send(Packet.Create(Packet.Types.Pong, new JsonObject
                {
                    ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                }));
                break;
            case Packet.Types.Notification:
                if (!conn.IsPaired) return; // 未配对不处理业务包
                HandleBusiness(conn, packet);
                break;
            case Packet.Types.StateInterruptionFilter:
                if (!conn.IsPaired) return;
                InterruptionFilterChanged?.Invoke(
                    packet.Body["filter"]?.GetValue<int>() ?? 2,
                    packet.Body["canSet"]?.GetValue<bool>() ?? false);
                break;
            case Packet.Types.CommandSetWindowsToast:
                if (!conn.IsPaired) return;
                // 手机端点了开关：电脑更新配置后再回 state.windowsToast
                WindowsToastRequested?.Invoke(packet.Body["enable"]?.GetValue<bool>() ?? true);
                break;
            case Packet.Types.StateWindowsToast:
                // 电脑自己就是该状态的权威来源，忽略手机回传
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
            conn.Send(Packet.Create(Packet.Types.PairOk, new JsonObject
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
            conn.Send(Packet.Create(Packet.Types.PairOk, new JsonObject
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
        var body = packet.Body;
        var isCancel = body["isCancel"]?.GetValue<bool>() ?? false;

        var update = new NotificationUpdate
        {
            Key = body["id"]?.GetValue<string>() ?? "",
            IsCancel = isCancel,
            IsSync = body["isSync"]?.GetValue<bool>() ?? false,
            IsOngoing = body["isOngoing"]?.GetValue<bool>() ?? false,
            Source = body["appName"]?.GetValue<string>()
                     ?? body["packageName"]?.GetValue<string>() ?? "未知应用",
            PackageName = body["packageName"]?.GetValue<string>() ?? "",
            Title = body["title"]?.GetValue<string>() ?? "",
            Content = body["text"]?.GetValue<string>() ?? "",
            Time = body["time"]?.GetValue<long>() ?? 0,
            IconBase64 = body["icon"]?.GetValue<string>() ?? ""
        };

        // 快照始终跟着手机通知的存亡走（含取消）
        NotificationChanged?.Invoke(update);

        // 历史记录只增不删：取消事件、连接后的批量补发、常驻通知都不写。
        // （当前手机端已经把常驻通知整体过滤掉了，IsOngoing 恒为 false，
        //  这里保留判断是为了以后若恢复「常驻只进快照」时不用再改电脑端。）
        if (isCancel || update.IsSync || update.IsOngoing) return;
        MessageReceived?.Invoke(MessageItem.FromNotification(body));
    }

    private static string GeneratePin()
    {
        Span<byte> bytes = stackalloc byte[4];
        RandomNumberGenerator.Fill(bytes);
        var value = BitConverter.ToUInt32(bytes) % 1_000_000;
        return value.ToString("D6");
    }
}
