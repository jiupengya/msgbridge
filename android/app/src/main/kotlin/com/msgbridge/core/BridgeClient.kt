package com.msgbridge.core

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import android.provider.Settings
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import org.json.JSONObject
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.Socket

/**
 * 简化版 TCP 客户端（参考 KDE Connect 的 LAN 后端 + NetworkPacket 行协议）。
 *
 * 职责：
 *  - 按指数退避自动连接电脑（IP:端口来自 Prefs）
 *  - 网络切换（Wi-Fi / 移动数据）时自动重连
 *  - 同步发送 JSON 行协议包
 *  - 读取并分发电脑发来的包（pair.ok / pair.reject / pong）
 */
class BridgeClient(private val context: Context, private val listener: Listener) {

    companion object {
        private const val TAG = "MsgBridge/Bridge"
    }

    interface Listener {
        fun onStateChanged(state: ConnectionState)
        /** 配对成功（收到 pair.ok 或连接后 Windows 直接放行） */
        fun onPaired()
        /** 配对失败（PIN 错误等） */
        fun onPairRejected()

        /** 电脑端要求清空手机通知栏 */
        fun onClearNotificationsRequested()

        /** 电脑端要求设置勿扰：0=开启勿扰，2=关闭勿扰 */
        fun onInterruptionFilterRequested(filter: Int)

        /** 任一端改了 Windows 气泡开关 */
        fun onWindowsToastRequested(enable: Boolean)

        /** 电脑端上报的 Windows 气泡开关状态 */
        fun onWindowsToastState(enable: Boolean)
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val writeLock = Mutex()

    @Volatile
    private var running = false
    private var socket: Socket? = null
    private var writer: BufferedWriter? = null
    private var connectJob: Job? = null

    private val connectivityManager =
        context.applicationContext.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) = restart()
        override fun onLost(network: Network) = restart()
    }

    val deviceId: String =
        Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown"

    val deviceName: String =
        Build.MANUFACTURER + " " + Build.MODEL

    fun start() {
        if (running) return
        running = true
        try {
            connectivityManager.registerDefaultNetworkCallback(networkCallback)
        } catch (e: Exception) {
            // 个别 ROM 不支持时忽略，仅影响断线后的及时重连
        }
        connectJob = scope.launch { connectLoop() }
    }

    fun stop() {
        running = false
        connectJob?.cancel()
        connectJob = null
        try {
            connectivityManager.unregisterNetworkCallback(networkCallback)
        } catch (e: Exception) {
        }
        closeSocket()
    }

    /** 网络变化/设置变更时断开当前连接；connectLoop 下一次循环会自动重连 */
    private fun restart() {
        if (!running) return
        // 只断开 socket：readLoop 随之退出，connectLoop 进入下一次循环重连。
        // 不能在这里 cancel + 重启 loop —— 旧 loop 可能正阻塞在 connect() 上，
        // 协作式取消无法中断它，会造成两个连接循环并发。
        closeSocket()
    }

    private fun hasNetwork(): Boolean {
        return try {
            val network = connectivityManager.activeNetwork
            network != null
        } catch (e: Exception) {
            true
        }
    }

    private suspend fun connectLoop() {
        var backoff = 1000L
        while (running) {
            val host = Prefs.host.trim()
            val port = Prefs.port

            if (host.isEmpty() || port <= 0) {
                listener.onStateChanged(ConnectionState.Disconnected)
                delay(5000)
                continue
            }
            if (!hasNetwork()) {
                listener.onStateChanged(ConnectionState.Disconnected)
                delay(5000)
                continue
            }

            listener.onStateChanged(ConnectionState.Connecting)
            try {
                val s = Socket()
                Log.i(TAG, "connecting to $host:$port ...")
                s.connect(InetSocketAddress(host, port), 5000)
                s.tcpNoDelay = true
                socket = s
                writer = BufferedWriter(OutputStreamWriter(s.getOutputStream(), Charsets.UTF_8))
                backoff = 1000L
                Log.i(TAG, "connected to $host:$port")

                // 连接建立后总是先发配对请求；若电脑端已信任此设备会直接回 pair.ok
                listener.onStateChanged(
                    if (Prefs.paired) ConnectionState.Connected
                    else ConnectionState.AwaitingPair
                )
                sendPairRequest()
                readLoop(s)
            } catch (e: Exception) {
                // 连接失败或连接被断开
                Log.w(TAG, "connect to $host:$port failed: ${e.message ?: e.javaClass.simpleName}")
            } finally {
                closeSocket()
                listener.onStateChanged(ConnectionState.Disconnected)
            }

            delay(backoff)
            backoff = (backoff * 2).coerceAtMost(30_000L)
        }
    }

    private suspend fun readLoop(s: Socket) {
        val reader = BufferedReader(InputStreamReader(s.getInputStream(), Charsets.UTF_8))
        while (running) {
            val line = reader.readLine() ?: break
            if (line.isBlank()) continue
            val packet = BridgeProtocol.parse(line) ?: continue
            handlePacket(packet)
        }
    }

    private suspend fun handlePacket(packet: JSONObject) {
        val type = packet.optString("type")
        Log.i(TAG, "recv: $type")
        when (type) {
            BridgeProtocol.TYPE_PAIR_OK -> {
                val body = packet.optJSONObject("body")
                Prefs.paired = true
                Prefs.pairedDeviceId = body?.optString("deviceId") ?: deviceId
                Prefs.lastConnectedAt = System.currentTimeMillis()
                Log.i(TAG, "paired ok, device=${body?.optString("deviceName") ?: ""}")
                listener.onStateChanged(ConnectionState.Connected)
                listener.onPaired()
            }
            BridgeProtocol.TYPE_PAIR_REJECT -> {
                Prefs.paired = false
                listener.onStateChanged(ConnectionState.AwaitingPair)
                listener.onPairRejected()
            }
            BridgeProtocol.TYPE_PONG -> {
                // 保活回应，无需处理
            }
            BridgeProtocol.TYPE_STATE_WINDOWS_TOAST -> {
                val body = packet.optJSONObject("body")
                listener.onWindowsToastState(body?.optBoolean("enable", true) ?: true)
            }
            BridgeProtocol.TYPE_COMMAND_CLEAR_NOTIFICATIONS -> {
                listener.onClearNotificationsRequested()
            }
            BridgeProtocol.TYPE_COMMAND_SET_INTERRUPTION_FILTER -> {
                val body = packet.optJSONObject("body")
                listener.onInterruptionFilterRequested(
                    body?.optInt("filter", BridgeProtocol.FILTER_ALL) ?: BridgeProtocol.FILTER_ALL
                )
            }
            BridgeProtocol.TYPE_COMMAND_SET_WINDOWS_TOAST -> {
                val body = packet.optJSONObject("body")
                listener.onWindowsToastRequested(body?.optBoolean("enable", true) ?: true)
            }
        }
    }

    fun sendPairRequest() {
        scope.launch {
            sendPacket(
                BridgeProtocol.TYPE_PAIR_REQUEST,
                JSONObject()
                    .put("deviceId", deviceId)
                    .put("deviceName", deviceName)
                    .put("platform", "android")
                    .put("version", BridgeProtocol.PROTOCOL_VERSION)
            )
        }
    }

    /** 提交配对 PIN */
    fun confirmPin(pin: String) {
        scope.launch { sendPacket(BridgeProtocol.TYPE_PAIR_CONFIRM, JSONObject().put("pin", pin)) }
    }

    /** 同步发送一个包；返回是否成功 */
    suspend fun sendPacket(type: String, body: JSONObject = JSONObject()): Boolean {
        writeLock.withLock {
            val w = writer ?: return false
            return try {
                w.write(BridgeProtocol.serialize(type, body))
                w.flush()
                true
            } catch (e: Exception) {
                closeSocket()
                false
            }
        }
    }

    /** 非阻塞发送（供事件回调使用，如通知监听） */
    fun send(type: String, body: JSONObject = JSONObject()) {
        scope.launch { sendPacket(type, body) }
    }

    fun sendPing() {
        send(BridgeProtocol.TYPE_PING, JSONObject().put("time", System.currentTimeMillis()))
    }

    private fun closeSocket() {
        try {
            socket?.close()
        } catch (e: Exception) {
        }
        socket = null
        writer = null
    }

    fun release() {
        stop()
        scope.cancel()
    }
}
