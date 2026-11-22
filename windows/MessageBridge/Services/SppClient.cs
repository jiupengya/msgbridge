using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace MessageBridge.Services;

/// <summary>
/// 经典蓝牙 SPP 客户端。
///
/// 相比 BLE：
///  - 手机不用广播——配对后地址固定，电脑像连耳机一样直接连过去
///  - 不会有「广播被系统回收、扫描不到」的问题
///  - 连接建立只需打开一条 RFCOMM 会话
///
/// 协议是一行一条：发 `wifi`，手机回 `ok:&lt;ssid&gt;` 或 `err:&lt;原因&gt;`。
/// </summary>
public sealed class SppClient : IDisposable
{
    /// <summary>标准串口服务 UUID，和手机端 SppServer 保持一致。</summary>
    public static readonly Guid SppUuid = Guid.Parse("00001101-0000-1000-8000-00805F9B34FB");

    private StreamSocket? _socket;
    private DataReader? _reader;
    private DataWriter? _writer;

    public event Action<string>? Log;

    public bool IsConnected => _socket != null;

    /// <summary>连到已配对的手机。<paramref name="address"/> 是经典蓝牙 MAC（配对后固定）。</summary>
    public async Task<bool> ConnectAsync(
        ulong address, TimeSpan timeout, CancellationToken ct = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        try
        {
            var device = await BluetoothDevice.FromBluetoothAddressAsync(address)
                .AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);
            if (device == null)
            {
                Log?.Invoke("找不到已配对的手机");
                return false;
            }

            var services = await device
                .GetRfcommServicesForIdAsync(RfcommServiceId.FromUuid(SppUuid),
                    BluetoothCacheMode.Uncached)
                .AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);

            if (services.Services.Count == 0)
            {
                Log?.Invoke($"手机没开 SPP 服务（{services.Error}）");
                return false;
            }

            var service = services.Services[0];
            var socket = new StreamSocket();
            await socket.ConnectAsync(
                    service.ConnectionHostName, service.ConnectionServiceName)
                .AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);

            _socket = socket;
            _reader = new DataReader(socket.InputStream)
            {
                InputStreamOptions = InputStreamOptions.Partial
            };
            _writer = new DataWriter(socket.OutputStream);
            Log?.Invoke($"已连接 {device.Name}");
            return true;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"连接失败：{ex.Message}");
            await DisconnectAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>发一条指令并等待一行回复；失败返回 null。</summary>
    public async Task<string?> SendAsync(
        string command, TimeSpan timeout, CancellationToken ct = default)
    {
        if (_writer == null || _reader == null) return null;

        try
        {
            _writer.WriteString(command + "\n");
            await _writer.StoreAsync().AsTask().WaitAsync(timeout, ct).ConfigureAwait(false);

            var read = await _reader.LoadAsync(256).AsTask()
                .WaitAsync(timeout, ct).ConfigureAwait(false);
            if (read == 0) return null;

            var text = _reader.ReadString(read).Trim();
            Log?.Invoke($"<- {text}");
            return text;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"收发失败：{ex.Message}");
            await DisconnectAsync().ConfigureAwait(false);
            return null;
        }
    }

    /// <summary>连上 + 发 wifi 指令，一步到位。</summary>
    public async Task<string?> RequestWifiAsync(
        ulong address, TimeSpan timeout, CancellationToken ct = default)
    {
        if (!await ConnectAsync(address, timeout, ct).ConfigureAwait(false)) return null;
        var reply = await SendAsync("wifi", timeout, ct).ConfigureAwait(false);
        await DisconnectAsync().ConfigureAwait(false);
        return reply;
    }

    public Task DisconnectAsync()
    {
        try
        {
            _reader?.Dispose();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // 忽略
        }
        try
        {
            _socket?.Dispose();
        }
        catch
        {
            // 忽略
        }

        _reader = null;
        _writer = null;
        _socket = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
    }
}
