package com.msgbridge.core

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject
import com.msgbridge.service.BridgeService

/**
 * 桥接控制器：UI 与底层客户端的唯一入口（进程级单例）。
 */
object BridgeController {

    private val _state = MutableStateFlow(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state

    private val _peerName = MutableStateFlow("")
    /** 已配对电脑的设备名 */
    val peerName: StateFlow<String> = _peerName

    private val _serviceEnabled = MutableStateFlow(Prefs.serviceEnabled)
    /** 推送服务开关（界面与控制中心快捷开关共用，保证两边状态一致） */
    val serviceEnabled: StateFlow<Boolean> = _serviceEnabled

    private var client: BridgeClient? = null
    private var onPairedHook: (() -> Unit)? = null

    /** 注册配对成功回调（用于同步存量通知/短信），同一时刻只保留一个 */
    fun setOnPaired(hook: () -> Unit) {
        onPairedHook = hook
    }

    private val listener = object : BridgeClient.Listener {
        override fun onStateChanged(state: ConnectionState) {
            _state.value = state
        }

        override fun onPaired() {
            _peerName.value = client?.deviceName ?: ""
            onPairedHook?.invoke()
        }

        override fun onPairRejected() {
            // 留在 AwaitingPair 状态等待用户重新输入 PIN
        }
    }

    fun start(context: Context) {
        if (client == null) {
            client = BridgeClient(context.applicationContext, listener)
        }
        client?.start()
    }

    fun stop() {
        client?.stop()
        _state.value = ConnectionState.Disconnected
    }

    /** 修改 IP/端口或重置配对后，断开并立即重连 */
    fun restart() {
        client?.stop()
        client?.start()
    }

    fun release(context: Context) {
        client?.release()
        client = null
        _state.value = ConnectionState.Disconnected
    }

    fun isConnected(): Boolean = _state.value == ConnectionState.Connected

    /**
     * 开关推送服务（主界面开关与控制中心快捷开关都走这里）。
     * 开关状态同步到 StateFlow，两个入口看到的永远一致。
     */
    fun setServiceEnabled(context: Context, enabled: Boolean) {
        Prefs.serviceEnabled = enabled
        _serviceEnabled.value = enabled
        if (enabled) {
            BridgeService.start(context)
        } else {
            BridgeService.stop(context)
        }
    }

    /** 提交配对 PIN */
    fun confirmPin(pin: String) {
        Prefs.paired = false
        client?.confirmPin(pin)
    }

    /** 重新发起配对请求（用户取消/失败后重试） */
    fun retryPair() {
        client?.sendPairRequest()
    }

    /** 非阻塞发送 */
    fun send(type: String, body: JSONObject = JSONObject()) {
        client?.send(type, body)
    }
}
