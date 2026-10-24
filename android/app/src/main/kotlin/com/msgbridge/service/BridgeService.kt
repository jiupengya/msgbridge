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
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.launch

/**
 * 常驻前台服务：保持 TCP 长连接（参考 android-screen-off-survival 三件套）。
 */
class BridgeService : Service() {

    companion object {
        private const val TAG = "MsgBridge/Svc"
        private const val CHANNEL_ID = "bridge_service"
        private const val NOTIFICATION_ID = 1001
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

    /** 前台通知节流：1 秒内不重复刷新（高频状态抖动时避免通知栏闪烁） */
    private var lastNotifyAt = 0L
    private var pendingText: String? = null

    private val wakeLock: PowerManager.WakeLock by lazy {
        (getSystemService(Context.POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "msgbridge:bridge")
            .apply { setReferenceCounted(false) }
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

        startForeground(NOTIFICATION_ID, buildNotification(getString(R.string.bridge_notif_text_idle)))
        if (!wakeLock.isHeld) {
            wakeLock.acquire()
        }
        BridgeController.start(this)
        Log.i(TAG, "service started, target=${Prefs.host}:${Prefs.port}")

        // 配对成功：同步存量通知
        BridgeController.setOnPaired {
            NotificationListener.instance?.syncCurrentNotifications()
        }

        // 连接状态变化时更新常驻通知文本
        scope.launch {
            BridgeController.state.collectLatest { state ->
                val text = when (state) {
                    ConnectionState.Connected ->
                        getString(
                            R.string.bridge_notif_text_connected,
                            BridgeController.peerName.value.ifEmpty { Prefs.host }
                        )
                    ConnectionState.AwaitingPair -> getString(R.string.bridge_notif_text_pairing)
                    ConnectionState.Connecting -> getString(R.string.bridge_notif_text_connecting)
                    ConnectionState.Disconnected -> getString(R.string.bridge_notif_text_idle)
                }
                notify(text)
            }
        }

        return START_STICKY
    }

    override fun onDestroy() {
        BridgeController.stop()
        if (wakeLock.isHeld) {
            wakeLock.release()
        }
        scope.cancel()
        super.onDestroy()
    }

    private fun createChannel() {
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.bridge_channel_name),
            NotificationManager.IMPORTANCE_LOW // 不响铃不震动不弹横幅，但常驻通知栏
        ).apply {
            setShowBadge(false)
            enableVibration(false)
        }
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .createNotificationChannel(channel)
    }

    private fun notify(text: String) {
        // 节流：1 秒内合并多次更新，避免连接状态抖动导致通知栏高频刷新
        val now = SystemClock.elapsedRealtime()
        if (now - lastNotifyAt >= 1000) {
            lastNotifyAt = now
            pushNotification(text)
        } else if (pendingText == null) {
            pendingText = text
            scope.launch {
                delay(1000)
                pendingText?.let {
                    pendingText = null
                    pushNotification(it)
                }
            }
        } else {
            pendingText = text
        }
    }

    private fun pushNotification(text: String) {
        (getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager)
            .notify(NOTIFICATION_ID, buildNotification(text))
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
