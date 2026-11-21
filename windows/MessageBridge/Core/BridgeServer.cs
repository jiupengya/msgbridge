using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MessageBridge.Core;

/// <summary>一个手机连接会话。</summary>
public sealed class BridgeConnection : IDisposable
{
    public TcpClient Tcp { get; }
    public string RemoteIp { get; }
    public string? DeviceId { get; set; }
    public string? DeviceName { get; set; }
    public bool IsPaired { get; set; }

    private readonly StreamWriter _writer;
    private readonly object _writeLock = new();

    public BridgeConnection(TcpClient tcp)
    {
        Tcp = tcp;
        RemoteIp = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString();
        _writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public bool Send(Packet packet)
    {
        lock (_writeLock)
        {
            try
            {
                _writer.Write(packet.Serialize());
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        try { _writer.Dispose(); } catch { }
        try { Tcp.Close(); } catch { }
    }
}

/// <summary>
/// TCP 服务端：监听手机连接，按行读取 JSON 包并分发。
/// </summary>
public sealed class BridgeServer : IDisposable
{
    public int Port { get; set; } = 17001;

    /// <summary>收到任意包（含配对包），在后台线程回调。</summary>
    public event Action<BridgeConnection, Packet>? PacketReceived;
    /// <summary>手机连上（会话建立后，后台线程回调）。</summary>
    public event Action<BridgeConnection>? Connected;
    /// <summary>设备断开（会话清理后）。</summary>
    public event Action<BridgeConnection>? Disconnected;

    private TcpListener? _listener;
    private CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, BridgeConnection> _connections = new();

    public IReadOnlyCollection<BridgeConnection> Connections => _connections.Values.ToList();

    /// <summary>
    /// 主动断开所有连接。断掉后读循环会走 finally，正常触发 <see cref="Disconnected"/>，
    /// 上层界面也就立刻从「已连接」变成「未连接」。
    /// </summary>
    public void DisconnectAll()
    {
        foreach (var conn in _connections.Values.ToList())
        {
            try { conn.Dispose(); } catch { }
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        // 退出时可能被调用多次（托盘退出 → OnExit），CTS 释放后再 Cancel 会抛异常
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        try { _listener?.Stop(); } catch { }
        var snapshot = _connections.Values.ToList();
        _connections.Clear();
        foreach (var c in snapshot)
        {
            try { c.Dispose(); } catch { }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var tcp = await _listener!.AcceptTcpClientAsync(token);
                var conn = new BridgeConnection(tcp);
                _ = HandleClientAsync(conn, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 个别连接失败不影响监听
            }
        }
    }

    private async Task HandleClientAsync(BridgeConnection conn, CancellationToken token)
    {
        var id = Guid.NewGuid();
        _connections[id] = conn;
        try
        {
            Connected?.Invoke(conn);
        }
        catch
        {
            // 状态回调异常不影响连接
        }

        try
        {
            using var reader = new StreamReader(conn.Tcp.GetStream(), new UTF8Encoding(false));
            while (!token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(token);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var packet = Packet.TryParse(line);
                if (packet != null)
                {
                    try { PacketReceived?.Invoke(conn, packet); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 连接异常断开
        }
        finally
        {
            try { conn.Dispose(); } catch { }
            _connections.TryRemove(id, out _);
            try { Disconnected?.Invoke(conn); } catch { }
        }
    }

    private volatile bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts.Dispose(); } catch { }
    }
}
