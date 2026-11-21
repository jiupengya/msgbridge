package com.msgbridge.service

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothGatt
import android.bluetooth.BluetoothGattCharacteristic
import android.bluetooth.BluetoothGattDescriptor
import android.bluetooth.BluetoothGattServer
import android.bluetooth.BluetoothGattServerCallback
import android.bluetooth.BluetoothGattService
import android.bluetooth.BluetoothManager
import android.bluetooth.le.AdvertiseCallback
import android.bluetooth.le.AdvertiseData
import android.bluetooth.le.AdvertiseSettings
import android.bluetooth.le.BluetoothLeAdvertiser
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.ParcelUuid
import android.util.Log
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.util.UUID

/**
 * BLE 外设：电脑在 WiFi 断开时通过这条带外通道把指令送进来。
 *
 * 为什么不用经典蓝牙 SPP：WinRT 的 GATT API 更干净，Android 前台服务里
 * 维持 GattServer + 广播也比 RFCOMM accept 循环省心。
 *
 * 报文刻意做得很短 —— BLE 默认 MTU 只有 23（有效载荷 20 字节），
 * 我们不下发 JSON，只发几个单词，回包也压在 20 字节内。
 */
class BleServer(
    private val context: Context,
    private val scope: CoroutineScope,
    /** 收到 `wifi` 指令时执行，返回要回给电脑的一行文本 */
    private val onWifiRequest: suspend () -> String,
) {

    companion object {
        private const val TAG = "MsgBridge/BLE"

        /** 自定义 128 位 UUID，避免和系统服务撞车 */
        val SERVICE_UUID: UUID = UUID.fromString("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c01")
        val CMD_UUID: UUID = UUID.fromString("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c02")
        val STATE_UUID: UUID = UUID.fromString("6ba1d3e0-2f5c-4e8a-9c1d-7a3b5e2f8c03")
        private val CCCD_UUID: UUID = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")

        /** Android 12 起蓝牙要运行时权限 */
        fun hasPermissions(context: Context): Boolean {
            if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) {
                return ContextCompat.checkSelfPermission(
                    context, Manifest.permission.BLUETOOTH
                ) == PackageManager.PERMISSION_GRANTED
            }
            return ContextCompat.checkSelfPermission(
                context, Manifest.permission.BLUETOOTH_CONNECT
            ) == PackageManager.PERMISSION_GRANTED &&
                ContextCompat.checkSelfPermission(
                    context, Manifest.permission.BLUETOOTH_ADVERTISE
                ) == PackageManager.PERMISSION_GRANTED
        }
    }

    private val manager =
        context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager

    private var server: BluetoothGattServer? = null
    private var advertiser: BluetoothLeAdvertiser? = null
    private var stateChar: BluetoothGattCharacteristic? = null

    @Volatile
    private var notifyDevice: BluetoothDevice? = null

    @Volatile
    private var running = false

    /** 状态文本：给刚连上来的电脑读一次 */
    @Volatile
    private var lastState: String = "idle"

    fun start() {
        if (running) return
        val adapter = manager?.adapter ?: run {
            Log.w(TAG, "no bluetooth adapter")
            return
        }
        if (!adapter.isEnabled) {
            Log.w(TAG, "bluetooth disabled")
            return
        }
        if (!hasPermissions(context)) {
            Log.w(TAG, "bluetooth permission not granted")
            return
        }

        running = true
        try {
            openServer()
            startAdvertising(adapter)
            Log.i(TAG, "BLE server started")
        } catch (e: SecurityException) {
            running = false
            Log.w(TAG, "BLE start denied: ${e.message}")
        } catch (e: Exception) {
            running = false
            Log.w(TAG, "BLE start failed: ${e.message}")
        }
    }

    fun stop() {
        running = false
        notifyDevice = null
        runCatching { advertiser?.stopAdvertising(advertiseCallback) }
        advertiser = null
        runCatching { server?.close() }
        server = null
        stateChar = null
        Log.i(TAG, "BLE server stopped")
    }

    /** 把一行状态推给已订阅的电脑（没有订阅者就只存着） */
    fun publishState(text: String) {
        lastState = text.take(20)
        val device = notifyDevice ?: return
        val ch = stateChar ?: return
        val srv = server ?: return
        try {
            ch.value = text.toByteArray(Charsets.UTF_8)
            srv.notifyCharacteristicChanged(device, ch, false)
        } catch (e: Exception) {
            Log.w(TAG, "notify failed: ${e.message}")
        }
    }

    // ---------------- GATT ----------------

    private fun openServer() {
        val srv = manager?.openGattServer(context, serverCallback) ?: run {
            Log.w(TAG, "openGattServer returned null")
            return
        }
        val service = BluetoothGattService(
            SERVICE_UUID, BluetoothGattService.SERVICE_TYPE_PRIMARY
        )
        val cmd = BluetoothGattCharacteristic(
            CMD_UUID,
            BluetoothGattCharacteristic.PROPERTY_WRITE or
                BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
            BluetoothGattCharacteristic.PERMISSION_WRITE
        )
        val state = BluetoothGattCharacteristic(
            STATE_UUID,
            BluetoothGattCharacteristic.PROPERTY_READ or
                BluetoothGattCharacteristic.PROPERTY_NOTIFY,
            BluetoothGattCharacteristic.PERMISSION_READ
        )
        state.addDescriptor(
            BluetoothGattDescriptor(
                CCCD_UUID,
                BluetoothGattDescriptor.PERMISSION_READ or
                    BluetoothGattDescriptor.PERMISSION_WRITE
            )
        )
        service.addCharacteristic(cmd)
        service.addCharacteristic(state)
        srv.addService(service)
        server = srv
        stateChar = state
    }

    private val serverCallback = object : BluetoothGattServerCallback() {
        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            Log.i(TAG, "conn state=$newState status=$status")
            if (newState == BluetoothGatt.STATE_DISCONNECTED) {
                if (notifyDevice?.address == device.address) notifyDevice = null
            }
        }

        override fun onCharacteristicReadRequest(
            device: BluetoothDevice,
            requestId: Int,
            offset: Int,
            characteristic: BluetoothGattCharacteristic,
        ) {
            val value = if (characteristic.uuid == STATE_UUID) {
                lastState.toByteArray(Charsets.UTF_8)
            } else {
                ByteArray(0)
            }
            try {
                server?.sendResponse(
                    device, requestId, BluetoothGatt.GATT_SUCCESS, offset, value
                )
            } catch (e: Exception) {
                Log.w(TAG, "sendResponse(read) failed: ${e.message}")
            }
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray?,
        ) {
            val command = value?.toString(Charsets.UTF_8)?.trim().orEmpty()
            Log.i(TAG, "write from ${device.address}: $command")
            if (responseNeeded) {
                try {
                    server?.sendResponse(
                        device, requestId, BluetoothGatt.GATT_SUCCESS, offset, null
                    )
                } catch (e: Exception) {
                    Log.w(TAG, "sendResponse(write) failed: ${e.message}")
                }
            }
            notifyDevice = device
            handleCommand(device, command)
        }

        override fun onDescriptorWriteRequest(
            device: BluetoothDevice,
            requestId: Int,
            descriptor: BluetoothGattDescriptor,
            preparedWrite: Boolean,
            responseNeeded: Boolean,
            offset: Int,
            value: ByteArray?,
        ) {
            if (descriptor.uuid == CCCD_UUID) {
                val subscribed = value?.let {
                    it.isNotEmpty() && it[0] == BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE[0]
                } ?: false
                notifyDevice = if (subscribed) device else null
                try {
                    server?.sendResponse(
                        device, requestId, BluetoothGatt.GATT_SUCCESS, offset, null
                    )
                } catch (e: Exception) {
                    Log.w(TAG, "sendResponse(cccd) failed: ${e.message}")
                }
            } else {
                try {
                    server?.sendResponse(
                        device, requestId, BluetoothGatt.GATT_SUCCESS, offset, null
                    )
                } catch (e: Exception) {
                    Log.w(TAG, "sendResponse(desc) failed: ${e.message}")
                }
            }
        }
    }

    // ---------------- 指令 ----------------

    private fun handleCommand(device: BluetoothDevice, command: String) {
        when {
            command.startsWith("wifi") -> {
                publishState("busy")
                scope.launch {
                    val reply = try {
                        onWifiRequest()
                    } catch (e: Exception) {
                        "err:" + (e.message ?: "unknown").take(12)
                    }
                    publishState(reply)
                }
            }
            command.startsWith("status") -> publishState(lastState)
            command.startsWith("ping") -> publishState("pong")
            else -> publishState("err:cmd")
        }
    }

    // ---------------- 广播 ----------------

    private fun startAdvertising(adapter: BluetoothAdapter) {
        val adv = adapter.bluetoothLeAdvertiser ?: run {
            Log.w(TAG, "no LE advertiser")
            return
        }
        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_HIGH)
            .setConnectable(true)
            .setTimeout(0)
            .build()
        val data = AdvertiseData.Builder()
            .addServiceUuid(ParcelUuid(SERVICE_UUID))
            .setIncludeDeviceName(false)
            .build()
        adv.startAdvertising(settings, data, advertiseCallback)
        advertiser = adv
    }

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
            Log.i(TAG, "advertising started")
        }

        override fun onStartFailure(errorCode: Int) {
            Log.w(TAG, "advertising failed: $errorCode")
        }
    }
}
