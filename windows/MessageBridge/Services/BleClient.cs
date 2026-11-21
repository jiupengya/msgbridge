using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace MessageBridge.Services;

/// <summary>
/// 蓝牙带外通道：WiFi 断了 TCP 就没了，只有 BLE 还能把「打开 WiFi」送进手机。
///
/// 手机端是一个 BLE 外设（见 android/.../service/BleServer.kt），
/// 我们做 GATT 客户端：扫广播 → 连接 → 写指令 → 等通知回包。
/// 报文刻意保持极短，默认 MTU 下有效载荷只有 20 字节。
/// </summary>
public sealed class BleClient : IDisposable
{
    /// <summary>与 Android 端 BleServer 里的 UUID 必须一致。</summary>
    public static readonly Guid ServiceUuid = Guid.Parse("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c01");
    public static readonly Guid CommandUuid = Guid.Parse("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c02");
    public static readonly Guid StateUuid = Guid.Parse("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c03");

    private ulong? _lastAddress;
    private BluetoothLEDevice? _device;
    private GattSession? _session;

    public event Action<string>? Log;

    /// <summary>上次扫到的手机蓝牙地址（随机地址，仅供界面展示）。</summary>
    public ulong? LastKnownAddress => _lastAddress;

    /// <summary>
    /// 让手机连回目标 WiFi。返回手机回的那行状态（例如 <c>ok:jiupengya-5G</c>）。
    /// </summary>
    public async Task<string> RequestWifiAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var (device, command, state) = await ConnectAsync(ct).ConfigureAwait(false);

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            var text = Decode(args.CharacteristicValue);
            Log?.Invoke($"ble <- {text}");
            // 手机先回 busy，真正结果在后面；
            if (!string.Equals(text, "busy", StringComparison.Ordinal))
                received.TrySetResult(text);
        }

        state.ValueChanged += OnValueChanged;
        try
        {
            if (state.CanNotify())
            {
                var cccd = await state.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
                if (cccd != GattCommunicationStatus.Success)
                    Log?.Invoke($"ble: 订阅通知失败 {cccd}");
            }

            var payload = CryptographicBuffer.ConvertStringToBinary("wifi", BinaryStringEncoding.Utf8);
            var write = await command.WriteValueWithResultAsync(payload, GattWriteOption.WriteWithResponse);
            Log?.Invoke($"ble -> wifi ({write.Status})");
            if (write.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException($"写指令失败：{write.Status}");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await received.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"等了 {timeout.TotalSeconds:0} 秒没收到手机回复");
            }
        }
        finally
        {
            state.ValueChanged -= OnValueChanged;
        }
    }

    /// <summary>只发 ping，确认通道是否通。</summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var (_, command, state) = await ConnectAsync(ct).ConfigureAwait(false);
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
            => received.TrySetResult(Decode(args.CharacteristicValue));

        state.ValueChanged += OnValueChanged;
        try
        {
            if (state.CanNotify())
            {
                await state.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify);
            }
            var payload = CryptographicBuffer.ConvertStringToBinary("ping", BinaryStringEncoding.Utf8);
            var write = await command.WriteValueWithResultAsync(payload, GattWriteOption.WriteWithResponse);
            if (write.Status != GattCommunicationStatus.Success) return false;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var text = await received.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            return text == "pong";
        }
        catch
        {
            return false;
        }
        finally
        {
            state.ValueChanged -= OnValueChanged;
        }
    }

    private async Task<(BluetoothLEDevice Device, GattCharacteristic Command, GattCharacteristic State)>
        ConnectAsync(CancellationToken ct)
    {
        // Android 广播时用的是可解析随机地址，每次扫描拿到的地址都不一样，
        // 所以缓存的 device 一旦连不上就得丢掉重扫。
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return await ConnectOnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                last = ex;
                Log?.Invoke($"ble: 连接失败（{ex.Message}），重扫重试");
                ResetDevice();
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("蓝牙连接失败");
    }

    private async Task<(BluetoothLEDevice Device, GattCharacteristic Command, GattCharacteristic State)>
        ConnectOnceAsync(CancellationToken ct)
    {
        if (_device == null)
        {
            var address = await ScanAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
            _lastAddress = address;
            Log?.Invoke($"ble: 连接 0x{address:X12}");

            _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("找不到手机蓝牙设备");

            _device.ConnectionStatusChanged += (_, _) =>
                Log?.Invoke($"ble: 连接状态 {_device?.ConnectionStatus}");

            // Windows 默认在两次 GATT 操作之间断开空闲连接，重连期间
            // ConnectionStatus 会疯狂抖动，服务发现很容易撞上超时。
            // 显式持有 GattSession 把链路留住。
            try
            {
                _session = await GattSession
                    .FromDeviceIdAsync(_device.BluetoothDeviceId)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
                if (_session != null) _session.MaintainConnection = true;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"ble: 建会话失败（{ex.Message}），继续尝试");
            }
        }

        // 刚拿到的 device 往往还是 Disconnected，直接去发现服务会拿到 Unreachable
        for (var i = 0; i < 30 && _device.ConnectionStatus != BluetoothConnectionStatus.Connected; i++)
            await Task.Delay(100, ct).ConfigureAwait(false);

        // 一次拿全部服务再自己挑：按 UUID 单查在部分适配器上会返回空/超时
        GattDeviceServicesResult? services = null;
        for (var i = 0; i < 3; i++)
        {
            services = await _device.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (services.Status == GattCommunicationStatus.Success && services.Services.Count > 0) break;
            Log?.Invoke($"ble: 服务发现 {services.Status}，重试");
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        if (services == null || services.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"没找到 GATT 服务：{services?.Status}");

        var service = services.Services.FirstOrDefault(s => s.Uuid == ServiceUuid)
            ?? throw new InvalidOperationException("没找到我们的 GATT 服务");

        // 特征同样整体取再挑，单查偶发返回空
        GattCharacteristicsResult? characteristics = null;
        for (var i = 0; i < 3; i++)
        {
            characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            if (characteristics.Status == GattCommunicationStatus.Success &&
                characteristics.Characteristics.Count > 0)
            {
                break;
            }
            Log?.Invoke($"ble: 特征发现 {characteristics.Status}，重试");
            await Task.Delay(400, ct).ConfigureAwait(false);
        }

        var command = characteristics?.Characteristics.FirstOrDefault(c => c.Uuid == CommandUuid);
        var state = characteristics?.Characteristics.FirstOrDefault(c => c.Uuid == StateUuid);
        if (command == null || state == null)
            throw new InvalidOperationException("GATT 特征缺失");

        return (_device, command, state);
    }

    private async Task<ulong> ScanAsync(TimeSpan timeout, CancellationToken ct)
    {
        var found = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active
        };
        watcher.Received += (_, e) =>
        {
            foreach (var uuid in e.Advertisement.ServiceUuids)
            {
                if (uuid != ServiceUuid) continue;
                Log?.Invoke($"ble: 扫到 0x{e.BluetoothAddress:X12} rssi={e.RawSignalStrengthInDBm}");
                found.TrySetResult(e.BluetoothAddress);
                return;
            }
        };
        watcher.Start();
        Log?.Invoke("ble: 开始扫描广播");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            return await found.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("没扫到手机的蓝牙广播，确认手机蓝牙已开、推送服务在运行");
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static string Decode(IBuffer buffer)
    {
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer))
        {
            reader.ReadBytes(bytes);
        }
        return Encoding.UTF8.GetString(bytes).Trim();
    }

    public void Dispose()
    {
        ResetDevice();
    }

    private void ResetDevice()
    {
        try
        {
            if (_session != null)
            {
                _session.MaintainConnection = false;
                _session.Dispose();
            }
        }
        catch
        {
            // 断开阶段忽略
        }
        _session = null;

        try
        {
            if (_device != null)
            {
                _device.Dispose();
            }
        }
        catch
        {
            // 断开阶段忽略
        }
        _device = null;
    }
}

internal static class GattCharacteristicExtensions
{
    public static bool CanNotify(this GattCharacteristic characteristic)
        => characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify);
}
