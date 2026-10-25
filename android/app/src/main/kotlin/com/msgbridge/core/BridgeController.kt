package com.msgbridge.core

import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.View
import android.view.WindowManager
import com.msgbridge.service.BridgeService
import com.msgbridge.service.NotificationListener
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject

/**
 * 桥接控制器：UI 与底层客户端的唯一入口（进程级单例）。
 *
 * 两个开关彼此独立，互不影响：
 *  - Windows 气泡开关：电脑是否弹系统 Toast，状态存在电脑 config.json，
 *    由电脑下发 state.windowsToast，手机点开关发 command.setWindowsToast。
 *  - 手机勿扰开关：手机系统勿扰，电脑下发 command.setInterruptionFilter，
 *    手机上报 state.interruptionFilter。勿扰**不再**拦截通知推送。
 */
object BridgeController {

    private const val TAG = "MsgBridge/Controller"

    private val _state = MutableStateFlow(ConnectionState.Disconnected)
    val state: StateFlow<ConnectionState> = _state

    private val _peerName = MutableStateFlow("")
    /** 已配对电脑的设备名 */
    val peerName: StateFlow<String> = _peerName

    private val _serviceEnabled = MutableStateFlow(Prefs.serviceEnabled)
    /** 推送服务开关（界面与控制中心快捷开关共用，保证两边状态一致） */
    val serviceEnabled: StateFlow<Boolean> = _serviceEnabled

    private val _windowsToast = MutableStateFlow(true)
    /** 电脑是否弹系统气泡（状态来自电脑上报） */
    val windowsToast: StateFlow<Boolean> = _windowsToast

    private val _interruptionFilter = MutableStateFlow(BridgeProtocol.FILTER_ALL)
    /** 手机当前勿扰状态：0=勿扰，2=正常 */
    val interruptionFilter: StateFlow<Int> = _interruptionFilter

    private val _canSetDnd = MutableStateFlow(false)
    /** 是否有「勿扰访问权限」，没有就改不了系统勿扰 */
    val canSetDnd: StateFlow<Boolean> = _canSetDnd

    private var appContext: Context? = null
    private var client: BridgeClient? = null
    private var onPairedHook: (() -> Unit)? = null

    /** 注册配对成功回调（用于同步存量通知/上报状态），同一时刻只保留一个 */
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

        override fun onClearNotificationsRequested() {
            Log.i(TAG, "clear notifications requested")
            NotificationListener.instance?.clearAllNotifications()
        }

        override fun onInterruptionFilterRequested(filter: Int) {
            Log.i(TAG, "set interruption filter requested: $filter")
            setInterruptionFilter(filter)
        }

        override fun onWindowsToastRequested(enable: Boolean) {
            // 电脑端已经改完配置，这里只需要更新 UI 上的开关状态
            _windowsToast.value = enable
        }

        override fun onWindowsToastState(enable: Boolean) {
            _windowsToast.value = enable
        }

        override fun onOpenNotificationRequested(key: String, packageName: String) {
            openNotification(key, packageName)
        }
    }

    fun start(context: Context) {
        appContext = context.applicationContext
        if (client == null) {
            client = BridgeClient(context.applicationContext, listener)
        }
        refreshDndState()
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
        appContext = null
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

    // ---------------- Windows 气泡开关 ----------------

    /** 手机端点开关：发指令给电脑，等电脑回 state.windowsToast 确认 */
    fun requestWindowsToast(enable: Boolean) {
        send(
            BridgeProtocol.TYPE_COMMAND_SET_WINDOWS_TOAST,
            JSONObject().put("enable", enable)
        )
    }

    // ---------------- 打开通知 / 应用 ----------------

    /** 临时叠加窗口挂多久再真正 startActivity（等系统把它记到本应用名下）。 */
    private const val OVERLAY_ANCHOR_SETTLE_MS = 120L

    /** 启动之后多久摘掉叠加窗口（给启动过程留点余量）。 */
    private const val OVERLAY_ANCHOR_LINGER_MS = 500L

    /**
     * 是否有「显示在其他应用的上层」权限。
     * 不是用来画悬浮窗的，而是「从后台打开应用」唯一能拿到的用户豁免，详见
     * [startActivityAllowingBackground]。
     */
    fun hasOverlayPermission(): Boolean {
        val ctx = appContext ?: return false
        return try {
            Settings.canDrawOverlays(ctx)
        } catch (e: Exception) {
            false
        }
    }

    /**
     * 电脑端点了某条通知，让手机打开它。
     *
     * 两级策略：
     *  1. Android 14 及更早：通知还挂着就 send() 它的 contentIntent ——
     *     效果等于在手机上点这条通知，能跳到对应的聊天窗口/详情页。
     *  2. 兜底 / Android 15 起：用 packageName 启动应用。
     *
     * 为什么 Android 15 起不再走 PendingIntent：后台启动 Activity 的限制收紧了，
     * 判定用的是「PendingIntent 的创建者」（发通知的那个应用，比如小米推送进程），
     * 不是我们，所以 send() 会被系统静默拦掉，点了没有任何反应。
     * 自己 startActivity 才有机会过（配合悬浮窗权限，见 [startActivityAllowingBackground]）。
     */
    fun openNotification(key: String, packageName: String): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.VANILLA_ICE_CREAM &&
            key.isNotEmpty() &&
            NotificationListener.instance?.clickNotification(key) == true
        ) {
            Log.i(TAG, "opened notification via content intent: $key")
            return true
        }
        if (packageName.isEmpty()) {
            Log.w(TAG, "open notification: no key hit and no package name")
            return false
        }

        val ctx = appContext ?: return false
        return try {
            val intent = ctx.packageManager.getLaunchIntentForPackage(packageName)
            if (intent == null) {
                Log.w(TAG, "open notification: no launch intent for $packageName")
                false
            } else {
                intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                startActivityAllowingBackground(ctx, intent)
                Log.i(TAG, "opened app: $packageName")
                true
            }
        } catch (e: Exception) {
            Log.w(TAG, "open app failed: $packageName", e)
            false
        }
    }

    /**
     * 从后台拉起界面。
     *
     * Android 15 起「从后台启动界面」被严格限制，光有 SYSTEM_ALERT_WINDOW 权限
     * 还不够 —— 系统判定的是 `hasNonAppVisibleWindow`（当前是否真的有叠加窗口）。
     * 所以这里先临时挂一个 1x1 的透明悬浮窗，等系统把它记到本应用名下再 startActivity，
     * 启动完立刻摘掉。这是目前唯一不需要让用户开无障碍服务的做法。
     *
     * 没有这个权限就只能硬试，系统多半会静默拦掉（不抛异常，只在 logcat 里记一笔），
     * 所以界面那边要引导用户去开。
     */
    private fun startActivityAllowingBackground(ctx: Context, intent: Intent) {
        if (!hasOverlayPermission()) {
            Log.w(TAG, "no overlay permission, background activity start will likely be blocked")
            try {
                ctx.startActivity(intent)
            } catch (e: Exception) {
                Log.w(TAG, "startActivity failed", e)
            }
            return
        }

        Handler(Looper.getMainLooper()).post {
            val wm = ctx.getSystemService(Context.WINDOW_SERVICE) as WindowManager
            val params = WindowManager.LayoutParams(
                1, 1,
                WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
                WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE or
                    WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL,
                PixelFormat.TRANSLUCENT
            ).apply {
                gravity = Gravity.TOP or Gravity.START
                x = 0
                y = 0
            }

            val anchor = View(ctx)
            val added = try {
                wm.addView(anchor, params)
                true
            } catch (e: Exception) {
                Log.w(TAG, "add overlay anchor failed", e)
                false
            }

            // 给 system_server 一点时间把窗口记到本应用的 uid 名下
            Handler(Looper.getMainLooper()).postDelayed({
                try {
                    ctx.startActivity(intent)
                } catch (e: Exception) {
                    Log.w(TAG, "startActivity failed (anchor=$added)", e)
                }
                Handler(Looper.getMainLooper()).postDelayed({
                    if (added) {
                        try {
                            wm.removeView(anchor)
                        } catch (e: Exception) {
                            // 窗口可能已经跟着进程一起回收了
                        }
                    }
                }, OVERLAY_ANCHOR_LINGER_MS)
            }, if (added) OVERLAY_ANCHOR_SETTLE_MS else 0)
        }
    }

    // ---------------- 手机勿扰 ----------------

    /** 是否有勿扰访问权限（没有就改不了系统勿扰） */
    fun hasDndPermission(): Boolean {
        val ctx = appContext ?: return false
        return try {
            notificationManager(ctx).isNotificationPolicyAccessGranted
        } catch (e: Exception) {
            false
        }
    }

    /** 读取手机当前勿扰状态，返回协议值（0=勿扰，2=正常） */
    fun currentFilter(): Int {
        val ctx = appContext ?: return BridgeProtocol.FILTER_ALL
        return try {
            systemToProto(notificationManager(ctx).currentInterruptionFilter)
        } catch (e: Exception) {
            BridgeProtocol.FILTER_ALL
        }
    }

    /**
     * 电脑端/手机端下发勿扰指令：0=开启勿扰，2=关闭勿扰。
     * 设完之后读回系统真实值再上报；没权限就只上报状态、不改系统。
     */
    fun setInterruptionFilter(filter: Int): Boolean {
        val ctx = appContext ?: return false
        val nm = notificationManager(ctx)
        return try {
            if (!nm.isNotificationPolicyAccessGranted) {
                Log.w(TAG, "no notification policy access, cannot set filter")
                reportInterruptionState()
                return false
            }

            nm.setInterruptionFilter(protoToSystem(filter))

            // 有些 ROM（MIUI/HyperOS）会忽略这次设置，读回来确认一下，
            // 没生效就把真实状态上报给电脑端，别让两端的开关显示成假的
            val actual = nm.currentInterruptionFilter
            val applied = if (filter == BridgeProtocol.FILTER_DND) {
                actual != NotificationManager.INTERRUPTION_FILTER_ALL
            } else {
                actual == NotificationManager.INTERRUPTION_FILTER_ALL
            }
            if (!applied) {
                Log.w(TAG, "setInterruptionFilter had no effect: requested=$filter actual=$actual")
            }

            _interruptionFilter.value = currentFilter()
            reportInterruptionState()
            applied
        } catch (e: Exception) {
            Log.w(TAG, "set interruption filter failed", e)
            reportInterruptionState()
            false
        }
    }

    /** 本地刷新勿扰状态（界面回到前台、权限刚授予时调用） */
    fun refreshDndState() {
        _interruptionFilter.value = currentFilter()
        _canSetDnd.value = hasDndPermission()
    }

    /**
     * 上报手机勿扰状态给电脑（连接建立、状态变化时调用）。
     * 系统勿扰变化由 NotificationListenerService.onInterruptionFilterChanged 通知过来，
     * 那边直接调这里（NotificationManager.addOnInterruptionFilterChangedListener
     * 在新 API 上已不可用，所以不单独注册监听）。
     */
    fun reportInterruptionState() {
        val ctx = appContext ?: return
        val filter = currentFilter()
        val canSet = hasDndPermission()

        _interruptionFilter.value = filter
        _canSetDnd.value = canSet

        send(
            BridgeProtocol.TYPE_STATE_INTERRUPTION_FILTER,
            JSONObject()
                .put("filter", filter)
                .put("canSet", canSet)
        )
    }

    /**
     * 系统勿扰值 -> 协议值。
     * 协议里只分两种：0=勿扰（含「仅优先」「仅闹钟」等所有非 ALL 模式），2=正常。
     * UNKNOWN 说明这次读不到（还没授权时系统就返回它），按「正常」处理，
     * 免得界面上谎报成「已开启勿扰」。
     */
    private fun systemToProto(systemFilter: Int): Int = when (systemFilter) {
        NotificationManager.INTERRUPTION_FILTER_ALL,
        NotificationManager.INTERRUPTION_FILTER_UNKNOWN -> BridgeProtocol.FILTER_ALL
        else -> BridgeProtocol.FILTER_DND
    }

    /**
     * 协议值 -> 系统勿扰值。
     *
     * 0 用 PRIORITY 而不是 NONE：NONE 会把手机铃声、媒体一起静音（用户反馈
     * 「开个勿扰连静音都打开了」）。PRIORITY 只压通知，来电仍可以响。
     * 2 对应正常（ALL）。
     */
    private fun protoToSystem(filter: Int): Int =
        if (filter == BridgeProtocol.FILTER_DND) {
            NotificationManager.INTERRUPTION_FILTER_PRIORITY
        } else {
            NotificationManager.INTERRUPTION_FILTER_ALL
        }

    private fun notificationManager(context: Context): NotificationManager =
        context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
}
