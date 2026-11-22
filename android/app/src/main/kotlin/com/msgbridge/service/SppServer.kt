package com.msgbridge.service

import android.Manifest
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothServerSocket
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.util.UUID

/**
 * 经典蓝牙 SPP 服务端。
 *
 * 为什么不用 BLE：BLE 的外设必须一直广播，中心端才找得到；而经典蓝牙配对后
 * 地址固定，电脑可以像连耳机一样**直接连过来**，手机这边只需要开着服务端口
 * 被动等待，完全不用广播。
 *
 * 用标准串口服务 UUID 是有意的：Windows 认这个 UUID 才会给设备建
 * 「蓝牙链接上的标准串行 (COMx)」虚拟串口，电脑端就能用 System.IO.Ports
 * 走纯 Win32 收发，不必申请 bluetooth capability。
 *
 * 协议和 BLE 通道保持一致：一行一条指令 / 一行一条回复。
 */
class SppServer(
    private val context: Context,
    private val scope: CoroutineScope,
    /** 收到指令时执行，返回要回给电脑的一行文本 */
    private val onCommand: suspend (String) -> String,
) {

    companion object {
        private const val TAG = "MsgBridge/SPP"

        /** 标准串口服务 UUID：00001101-0000-1000-8000-00805F9B34FB */
        val SppUuid: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

        private const val ServiceName = "MessageBridge"
    }

    private var serverSocket: BluetoothServerSocket? = null
    private var acceptJob: Job? = null

    @Volatile
    private var running = false

    fun start() {
        if (running) return

        val adapter = (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter
        if (adapter == null) {
            Log.w(TAG, "no bluetooth adapter")
            return
        }
        if (!adapter.isEnabled) {
            Log.w(TAG, "bluetooth disabled")
            return
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_CONNECT) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            Log.w(TAG, "BLUETOOTH_CONNECT not granted")
            return
        }

        try {
            serverSocket = adapter.listenUsingRfcommWithServiceRecord(ServiceName, SppUuid)
            running = true
            acceptJob = scope.launch(Dispatchers.IO) { acceptLoop() }
            Log.i(TAG, "SPP server started, uuid=$SppUuid")
        } catch (e: Exception) {
            running = false
            Log.w(TAG, "SPP start failed: ${e.message}")
        }
    }

    fun stop() {
        running = false
        acceptJob?.cancel()
        acceptJob = null
        try {
            serverSocket?.close()
        } catch (e: Exception) {
            // 关闭阶段忽略
        }
        serverSocket = null
        Log.i(TAG, "SPP server stopped")
    }

    private suspend fun acceptLoop() {
        while (running) {
            val socket = try {
                serverSocket?.accept()
            } catch (e: Exception) {
                if (running) Log.w(TAG, "accept failed: ${e.message}")
                null
            }
            if (socket == null) break
            // 每个连接单独跑，互不阻塞
            scope.launch(Dispatchers.IO) { handle(socket) }
        }
    }

    private suspend fun handle(socket: BluetoothSocket) {
        val name = socket.remoteDevice?.name ?: socket.remoteDevice?.address ?: "unknown"
        Log.i(TAG, "connected from $name")
        try {
            val reader = BufferedReader(InputStreamReader(socket.inputStream, Charsets.UTF_8))
            val writer = OutputStreamWriter(socket.outputStream, Charsets.UTF_8)
            while (running) {
                val line = reader.readLine() ?: break
                val command = line.trim()
                if (command.isEmpty()) continue
                Log.i(TAG, "recv: $command")

                val reply = try {
                    onCommand(command)
                } catch (e: Exception) {
                    "err:" + (e.message ?: "unknown").take(20)
                }
                // 每条回复独立成行，电脑端按行读
                writer.write(reply + "\n")
                writer.flush()
                Log.i(TAG, "sent: $reply")
            }
        } catch (e: Exception) {
            Log.w(TAG, "session ended: ${e.message}")
        } finally {
            try {
                socket.close()
            } catch (e: Exception) {
                // 忽略
            }
        }
    }
}
