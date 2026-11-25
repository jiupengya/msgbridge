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
import android.provider.Settings
import android.util.Log
import com.msgbridge.R
import com.msgbridge.core.BridgeController
import com.msgbridge.core.Prefs
import com.msgbridge.core.WifiControl
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel

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

        /** 自动切 WiFi 失败时的提醒（需手动连接），单独 channel + id，用户能看见 */
        private const val WIFI_CHANNEL_ID = "wifi_request"
        private const val WIFI_FAILED_NOTIFICATION_ID = 1003

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

    /** 经典蓝牙 SPP：配对后电脑可以直接连，手机不需要广播。 */
    private var sppServer: SppServer? = null

    private fun startSppIfEnabled() {
        if (!Prefs.wifiFallbackEnabled) {
            Log.i(TAG, "wifi fallback disabled, SPP not started")
            return
        }
        if (sppServer != null) return
        sppServer = SppServer(applicationContext, scope) { command ->
            when {
                command.startsWith("ping") -> "pong"
                command.startsWith("wifi") -> {
                    val result = WifiControl.ensureOnTarget(
                        Prefs.targetSsid, Prefs.targetWifiPassword
                    )
                    Log.i(TAG, "spp wifi -> ${result.outcome} ${result.ssid} ${result.message}")
                    if (result.ok) {
                        "ok:${result.ssid}"
                    } else {
                        // 自动切换没成（Shizuku 没授权 / 目标网络不在范围内等），
                        // 给用户一条能看见的通知，提示手动连一下
                        notifyWifiFailed(Prefs.targetSsid)
                        "err:${result.message.take(12)}"
                    }
                }
                else -> "err:cmd"
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
        startSppIfEnabled()
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
        sppServer?.stop()
        sppServer = null
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

    /**
     * 自动切 WiFi 没成功时提示用户手动连一下。
     * 点通知直接跳到系统的 WiFi 设置页。
     */
    private fun notifyWifiFailed(ssid: String) {
        val intent = Intent(Settings.ACTION_WIFI_SETTINGS)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        val pending = PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE)

        val n = Notification.Builder(this, WIFI_CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(getString(R.string.app_name))
            .setContentText("需手动连接 $ssid")
            .setContentIntent(pending)
            .setAutoCancel(true)
            .setShowWhen(false)
            .build()
        notificationManager.notify(WIFI_FAILED_NOTIFICATION_ID, n)
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
