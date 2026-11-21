package com.msgbridge.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.drawable.Icon
import android.os.IBinder
import android.os.PowerManager
import android.os.SystemClock
import android.util.Log
import com.msgbridge.R
import com.msgbridge.core.BridgeController
import com.msgbridge.core.ConnectionState
import com.msgbridge.core.Prefs
import com.msgbridge.core.WifiControl
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * 常驻前台服务：保持 TCP 长连接（参考 android-screen-off-survival 三件套）。
 */
class BridgeService : Service() {

    companion object {
        private const val TAG = "MsgBridge/Svc"

        /**
         * 前台服务必须挂一条通知，Android 不允许应用自己撤掉它（cancel 会被系统拒绝）。
         * 所以这条通知的定位是「能藏就藏」：优先级压到 MIN，用户还可以在系统的
         * 通知设置里把这个 channel 直接关掉（设置页提供了入口）。
         * v2 是为了把上一版 LOW 优先级的 channel 换掉 —— channel 的重要性创建后改不了。
         */
        private const val CHANNEL_ID = "bridge_service_v2"
        private const val LEGACY_CHANNEL_ID = "bridge_service"
        private const val NOTIFICATION_ID = 1001

        /** 「电脑请求连回 WiFi」的提示通知，单独的 channel 和 id，用户能看见 */
        private const val WIFI_CHANNEL_ID = "wifi_request"
        private const val WIFI_NOTIFICATION_ID = 1002

        const val ACTION_STOP = "com.msgbridge.action.STOP"

        fun start(context: Context) {
            context.startForegroundService(Intent(context, BridgeService::class.java))
        }

        fun stop(context: Context) {
            // 用 stopService 而不是 startService(ACTION_STOP)：
            // 从控制中心快捷开关（后台）启动服务会被系统限制，而停止不受此限制。
            context.stopService(Intent(context, BridgeService::class.java))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val notificationManager by lazy {
        getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
    }

    private val wakeLock: PowerManager.WakeLock by lazy {
        (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "msgbridge:bridge")
            .apply { setReferenceCounted(false) }
    }

    /**
     * 蓝牙带外通道：WiFi 断了只有它还能把「打开 WiFi」送进来。
     * 指令执行耗时要几秒，放到 IO 线程，别卡住 GATT 回调。
     */
    private var bleServer: BleServer? = null

    private fun startBleIfEnabled() {
        if (!Prefs.wifiFallbackEnabled) {
            Log.i(TAG, "wifi fallback disabled, BLE not started")
            return
        }
        if (bleServer != null) return
        if (!BleServer.hasPermissions(this)) {
            Log.i(TAG, "bluetooth permission missing, BLE not started")
            return
        }
        bleServer = BleServer(applicationContext, scope) {
            withContext(Dispatchers.IO) {
                // 先让用户看到一条提示，再动手
                notifyWifiRequest()
                try {
                    val result = WifiControl.ensureOnTarget(
                        Prefs.targetSsid, Prefs.targetWifiPassword
                    )
                    Log.i(TAG, "wifi request -> ${result.outcome} ${result.ssid} ${result.message}")
                    if (result.ok) "ok:${result.ssid}" else "err:${result.message.take(12)}"
                } finally {
                    // 执行很快（WiFi 关→开通常 1~2 秒），通知多留一会儿才看得见
                    delay(2_000)
                    cancelWifiRequest()
                }
            }
        }.also { it.start() }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }

        startForeground(NOTIFICATION_ID, buildNotification(""))
        // 常驻通知只是前台服务的「入场券」：挂上之后立刻撤掉，通知栏里看不见，
        // 但服务本身仍是前台状态，不会被系统当成后台服务回收。
        notificationManager.cancel(NOTIFICATION_ID)

        if (!wakeLock.isHeld) {
            wakeLock.acquire()
        }
        BridgeController.start(this)
        startBleIfEnabled()
        Log.i(TAG, "service started, target=${Prefs.host}:${Prefs.port}")

        // 配对成功：同步存量通知做快照，并上报手机勿扰状态
        BridgeController.setOnPaired {
            NotificationListener.instance?.syncCurrentNotifications()
            BridgeController.reportInterruptionState()
        }

        return START_STICKY
    }

    override fun onDestroy() {
        BridgeController.stop()
        bleServer?.stop()
        bleServer = null
        if (wakeLock.isHeld) {
            wakeLock.release()
        }
        scope.cancel()
        super.onDestroy()
    }

    private fun createChannel() {
        // 常驻服务的通知：挂上就被撤掉，用户看不到，优先级压到最低即可
        val service = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.bridge_channel_name),
            NotificationManager.IMPORTANCE_MIN
        ).apply {
            setShowBadge(false)
            enableVibration(false)
            setSound(null, null)
        }
        // 只在电脑请求连回 WiFi 时出现，要让用户注意到
        val wifi = NotificationChannel(
            WIFI_CHANNEL_ID,
            "WiFi 兜底",
            NotificationManager.IMPORTANCE_DEFAULT
        ).apply {
            setShowBadge(false)
        }
        notificationManager.createNotificationChannels(listOf(service, wifi))

        // 清掉上一版那条 LOW 优先级的 channel，免得在设置里多出一项
        runCatching { notificationManager.deleteNotificationChannel(LEGACY_CHANNEL_ID) }
    }

    /** 电脑请求连回 WiFi 之前先提示一下，执行完就撤掉 */
    private fun notifyWifiRequest() {
        val n = Notification.Builder(this, WIFI_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(getString(R.string.app_name))
            .setContentText("电脑请求连回 WiFi，正在处理…")
            .setOngoing(true)
            .setShowWhen(false)
            .build()
        notificationManager.notify(WIFI_NOTIFICATION_ID, n)
    }

    private fun cancelWifiRequest() {
        notificationManager.cancel(WIFI_NOTIFICATION_ID)
    }

    private fun buildNotification(text: String): Notification {
        val stopIntent = PendingIntent.getService(
            this,
            0,
            Intent(this, BridgeService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .addAction(
                Notification.Action.Builder(
                    Icon.createWithResource(this, R.drawable.ic_stat_notify),
                    getString(R.string.bridge_notif_stop),
                    stopIntent
                ).build()
            )
            .setOngoing(true)
            .setShowWhen(false)
            .build()
    }
}
