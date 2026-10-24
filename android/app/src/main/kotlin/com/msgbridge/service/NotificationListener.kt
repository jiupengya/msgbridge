package com.msgbridge.service

import android.app.KeyguardManager
import android.app.Notification
import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.Drawable
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.text.TextUtils
import android.util.Base64
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.graphics.createBitmap
import com.msgbridge.R
import com.msgbridge.core.BridgeController
import com.msgbridge.core.BridgeProtocol
import com.msgbridge.core.Prefs
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.util.Locale

/**
 * 通知监听服务（完整移植 KDE Connect NotificationsPlugin）：
 * 过滤用户看不到/不值得推的通知，提取完整字段（图标/会话/动作等）后推送到电脑端。
 *
 * 与参考实现一致的关键逻辑：
 * - 过滤：前台服务/常驻/仅本机/组摘要/媒体控制/低电量/自己的通知/被暂停应用
 * - posted 延迟 50ms 再发（防"刚弹出就被移除"的通知也推送）
 * - 图标：大图标 → 小图标 → 应用图标三级兜底，96x96 PNG base64
 */
class NotificationListener : NotificationListenerService() {

    /** 最近 posted、尚未发送的通知 key（配合延迟发送防闪烁） */
    private val postedNotifications = HashSet<String>()

    /** 已发送过的通知 key（用于判断 isUpdate / silent） */
    private val currentNotifications = HashSet<String>()

    /** key -> 待执行的延迟发送任务 */
    private val pendingTasks = HashMap<String, Runnable>()

    private val mainHandler = Handler(Looper.getMainLooper())

    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (!Prefs.pushNotifications) return
        if (!BridgeController.isConnected()) return
        // 注意：勿扰不再拦截推送 —— 勿扰开关只控制手机本机是否响铃，
        // 通知始终同步到电脑，由电脑端的「气泡开关」决定是否弹窗。

        // 仅锁屏推送（参考 KDE Connect 的 sendOnlyIfLocked）
        if (Prefs.pushOnlyWhenLocked) {
            val km = getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
            if (!km.isKeyguardLocked) return
        }

        val key = sbn.key
        synchronized(postedNotifications) {
            postedNotifications.add(key)
        }
        sendNotificationWithDelay(sbn)
    }

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        val key = sbn.key
        synchronized(postedNotifications) {
            postedNotifications.remove(key)
        }
        pendingTasks.remove(key)?.let { mainHandler.removeCallbacks(it) }

        if (!Prefs.pushNotifications) return
        if (!BridgeController.isConnected()) return
        if (!shouldForward(sbn)) return
        if (!currentNotifications.remove(key)) return // 没推过就不发 cancel

        val body = JSONObject()
            .put("id", key)
            .put("isCancel", true)
        BridgeController.send(BridgeProtocol.TYPE_NOTIFICATION, body)
    }

    /** 监听器被系统连接后，同步当前存量通知（silent=true，参考 sendCurrentNotifications） */
    override fun onListenerConnected() {
        super.onListenerConnected()
        syncCurrentNotifications()
        // 监听器重连时把勿扰状态也重新报一次
        BridgeController.reportInterruptionState()
    }

    /** 系统勿扰变化（用户在下拉面板手动切换）：上报给电脑 */
    override fun onInterruptionFilterChanged(interruptionFilter: Int) {
        super.onInterruptionFilterChanged(interruptionFilter)
        BridgeController.reportInterruptionState()
    }

    /**
     * 同步手机当前所有存活通知到电脑，用于重连后重建快照。
     * 带上 isSync=true，电脑端据此「不弹 Toast、不写历史」。
     *
     * 注意必须全量重发：不能按 currentNotifications 跳过，
     * 否则首次 onListenerConnected 时（尚未连上电脑）把这些 key 标记成已发送，
     * 真正连上后就永远补不回去了。
     */
    fun syncCurrentNotifications() {
        if (!Prefs.pushNotifications) return
        if (!BridgeController.isConnected()) return
        try {
            val notifications = activeNotifications ?: return
            for (n in notifications) {
                if (!shouldForward(n)) continue
                sendNotificationPacket(n, isCancel = false, isPreexisting = true, isSync = true)
            }
        } catch (e: SecurityException) {
            Log.w(TAG, "sync active notifications failed", e)
        }
    }

    /**
     * 清空手机通知栏里可清除的通知。
     * cancelAllNotifications 清不掉 Ongoing 常驻通知，那些会继续留在电脑的快照列表里。
     */
    fun clearAllNotifications() {
        try {
            cancelAllNotifications()
            Log.i(TAG, "cancelAllNotifications called")
        } catch (e: Exception) {
            Log.w(TAG, "clear all notifications failed", e)
        }
    }

    // ---------- 过滤（与 KDE Connect NotificationsPlugin 保持一致） ----------

    private fun shouldForward(sbn: StatusBarNotification): Boolean {
        val notification = sbn.notification
        val flags = notification.flags

        // 前台服务 / 常驻通知 / 仅本机 / 组摘要 / 媒体播放控制：都不推
        //
        // 常驻通知为什么也过滤：手机通知栏里它们大多数被系统折叠进「静默通知 / 更多通知」，
        // 用户在手机上看不到，推到电脑快照面板就成了一堆对不上号的噪音
        // （小米智能卡、跨屏协同、语音唤醒之类）。
        // 试过用 getRanking().importance 判断「是否真的可见」，但这些常驻通知普遍是
        // DEFAULT/LOW 级别，靠重要度区分不出来，所以整体不推。
        if ((flags and Notification.FLAG_FOREGROUND_SERVICE) != 0 ||
            (flags and Notification.FLAG_ONGOING_EVENT) != 0 ||
            (flags and Notification.FLAG_LOCAL_ONLY) != 0 ||
            (flags and NotificationCompat.FLAG_GROUP_SUMMARY) != 0 ||
            Notification.CATEGORY_TRANSPORT == notification.category
        ) {
            return false
        }

        val packageName = sbn.packageName
        if (packageName == packageNameOfSelf) {
            return false // 不推自己的通知
        }

        if ("com.android.systemui" == packageName) {
            if ("low_battery" == sbn.tag) {
                // 低电量通知每几秒重发一次，跳过（有独立电池状态）
                return false
            }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
                "MediaOngoingActivity" == notification.channelId
            ) {
                // 三星 OneUI 媒体播放常驻通知，跳过
                return false
            }
        }

        // 被暂停/隐藏的应用（专注模式、数字健康等）：手机上都看不到，不推
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            try {
                if (packageManager.isPackageSuspended(packageName)) {
                    return false
                }
            } catch (_: PackageManager.NameNotFoundException) {
                // 应用已卸载
            }
        }

        return true
    }

    // ---------- 发送 ----------

    /** 延迟 50ms 发送，期间若通知被移除则取消（参考 sendNotificationWithDelay） */
    private fun sendNotificationWithDelay(sbn: StatusBarNotification) {
        val key = sbn.key
        pendingTasks.remove(key)?.let { mainHandler.removeCallbacks(it) }
        val task = Runnable {
            synchronized(postedNotifications) {
                if (!postedNotifications.contains(key)) return@Runnable
                pendingTasks.remove(key)
            }
            if (!shouldForward(sbn)) return@Runnable
            sendNotificationPacket(sbn, isCancel = false, isPreexisting = false)
        }
        pendingTasks[key] = task
        mainHandler.postDelayed(task, NOTIFICATION_SYNC_DELAY_MS)
    }

    private fun sendNotificationPacket(
        sbn: StatusBarNotification,
        isCancel: Boolean,
        isPreexisting: Boolean,
        isSync: Boolean = false
    ) {
        val notification = sbn.notification
        val key = sbn.key
        val flags = notification.flags

        // 常驻通知（前台服务 / Ongoing）标记。
        // 注意：现在 shouldForward 已经把常驻通知整体过滤掉了，所以走到这里的
        // 通知这个值恒为 false；字段保留是为了以后如果需要「常驻只进快照」，
        // 只要把 shouldForward 里那两行去掉就能恢复。
        val isOngoing = (flags and Notification.FLAG_ONGOING_EVENT) != 0 ||
            (flags and Notification.FLAG_FOREGROUND_SERVICE) != 0

        val isUpdate = currentNotifications.contains(key)
        if (!isCancel && !isUpdate) {
            currentNotifications.add(key)
        }
        if (isCancel) {
            currentNotifications.remove(key)
        }

        val body = JSONObject()
            .put("id", key)
            .put("appName", appNameOf(sbn.packageName))
            .put("packageName", sbn.packageName)
            .put("time", sbn.postTime)
            .put("isClearable", sbn.isClearable)
            .put("isOngoing", isOngoing)
            .put("isSync", isSync)
            .put("silent", isPreexisting || (isUpdate && (flags and Notification.FLAG_ONLY_ALERT_ONCE) != 0))

        if (!isCancel) {
            // 图标：大图标 → 小图标 → 应用图标（96x96 PNG base64）
            val iconBitmap = extractIcon(sbn, notification)
            if (iconBitmap != null) {
                body.put("icon", bitmapToBase64(iconBitmap))
            }

            // 会话类通知（微信/QQ/Telegram 等）：补全 conversation / groupName
            val messagingStyle = NotificationCompat.MessagingStyle
                .extractMessagingStyleFromNotification(notification)
            val isGroupConversation = messagingStyle != null &&
                messagingStyle.isGroupConversation && messagingStyle.conversationTitle != null

            if (messagingStyle != null) {
                val conversation = JSONArray()
                for (m in messagingStyle.messages) {
                    val sender = m.person?.name?.toString() ?: getString(R.string.unknown_sender)
                    val content = m.text?.toString() ?: ""
                    conversation.put(JSONObject().put("sender", sender).put("content", content))
                }
                if (conversation.length() > 0) {
                    body.put("conversation", conversation)
                }
            }

            if (isGroupConversation) {
                // 群聊：title 用群名，text 用「发送人: 内容」，与手机端显示一致
                val groupName = messagingStyle!!.conversationTitle.toString()
                val last = messagingStyle.messages.lastOrNull()
                val sender = last?.person?.name?.toString() ?: getString(R.string.unknown_sender)
                val content = last?.text?.toString() ?: ""
                body.put("groupName", groupName)
                body.put("title", groupName)
                body.put("text", "$sender: $content")
            } else {
                val title = extrasString(notification, NotificationCompat.EXTRA_TITLE)
                if (title != null) body.put("title", title)
                val text = extractText(notification)
                if (text != null) body.put("text", text)
            }

            val ticker = getTickerText(notification)
            if (ticker.isNotEmpty()) body.put("ticker", ticker)

            // 动作按钮（排除回复动作）与可回复标记
            val actions = extractActions(notification)
            if (actions != null) body.put("actions", actions)
            if (hasReplyAction(notification)) {
                body.put("requestReplyId", key)
            }
        }

        BridgeController.send(BridgeProtocol.TYPE_NOTIFICATION, body)
    }

    private fun extractText(notification: Notification): String? {
        val extras = notification.extras
        if (extras.containsKey(NotificationCompat.EXTRA_BIG_TEXT)) {
            return extrasString(notification, NotificationCompat.EXTRA_BIG_TEXT)
        }
        return extrasString(notification, NotificationCompat.EXTRA_TEXT)
    }

    /** 参考 getTickerText：extras 拼接优先，tickerText 兜底 */
    private fun getTickerText(notification: Notification): String {
        var ticker = ""
        try {
            val extras = notification.extras
            val title = extrasString(notification, NotificationCompat.EXTRA_TITLE)
            val text = extrasString(notification, NotificationCompat.EXTRA_TEXT)
            if (title != null && !TextUtils.isEmpty(text)) {
                ticker = "$title: $text"
            } else if (title != null) {
                ticker = title
            } else if (text != null) {
                ticker = text
            }
        } catch (e: Exception) {
            Log.w(TAG, "parse extras failed", e)
        }
        if (ticker.isEmpty()) {
            ticker = notification.tickerText?.toString() ?: ""
        }
        return ticker
    }

    private fun extrasString(notification: Notification, key: String): String? {
        return notification.extras.getCharSequence(key)?.toString()
    }

    /** 动作按钮标题数组（排除带输入框的回复动作） */
    private fun extractActions(notification: Notification): JSONArray? {
        val actions = notification.actions ?: return null
        val arr = JSONArray()
        for (action in actions) {
            val title = action.title?.toString() ?: continue
            if (action.remoteInputs?.isNotEmpty() == true) continue // 回复动作单独处理
            arr.put(title)
        }
        return if (arr.length() > 0) arr else null
    }

    /** 找到带输入框的回复动作（第一个可用的） */
    private fun hasReplyAction(notification: Notification): Boolean {
        val actions = notification.actions ?: return false
        for (action in actions) {
            if (action.remoteInputs?.isNotEmpty() == true && action.actionIntent != null) {
                return true
            }
        }
        return false
    }

    // ---------- 图标提取（完整移植 NotificationsPlugin.extractIcon） ----------

    private fun extractIcon(sbn: StatusBarNotification, notification: Notification): Bitmap? {
        // 第一段：通知自带的大图标 / 小图标（最准，能反映应用当前的图标状态）
        try {
            val foreignContext = createPackageContext(sbn.packageName, 0)
            notification.getLargeIcon()?.let { icon ->
                iconToBitmap(foreignContext, icon)?.let { return it }
            }
            notification.getSmallIcon()?.let { icon ->
                iconToBitmap(foreignContext, icon)?.let { return it }
            }
        } catch (e: Exception) {
            // 旧通知的 Icon 对象可能已经失效，正常现象，往下走兜底
            Log.w(TAG, "icon: notification icon failed for ${sbn.packageName}", e)
        }

        // 第二段：应用图标兜底。
        // 连接后批量补发时，通知里挂了一段时间的 Icon 经常取不到（尤其会话类通知），
        // 少了这步快照面板上就只剩个灰方块 —— 主界面有图标、面板没有，就是这个原因。
        try {
            return drawableToBitmap(packageManager.getApplicationIcon(sbn.packageName))
        } catch (e: Exception) {
            Log.w(TAG, "icon: app icon failed for ${sbn.packageName}", e)
        }

        return null
    }

    private fun iconToBitmap(foreignContext: Context?, icon: Icon?): Bitmap? {
        icon ?: return null
        return drawableToBitmap(icon.loadDrawable(foreignContext))
    }

    /** 参考 drawableToBitmap：小图标放大到 96，过大图标缩到 96，合适尺寸保持原尺寸 */
    private fun drawableToBitmap(drawable: Drawable?): Bitmap? {
        drawable ?: return null
        return try {
            val res = if (drawable.intrinsicWidth > 128 || drawable.intrinsicHeight > 128) {
                createBitmap(96, 96)
            } else if (drawable.intrinsicWidth <= 64 || drawable.intrinsicHeight <= 64) {
                createBitmap(96, 96)
            } else {
                createBitmap(
                    drawable.intrinsicWidth.takeIf { it > 0 } ?: 96,
                    drawable.intrinsicHeight.takeIf { it > 0 } ?: 96
                )
            }
            val canvas = Canvas(res)
            drawable.setBounds(0, 0, res.width, res.height)
            drawable.draw(canvas)
            res
        } catch (e: Exception) {
            null
        }
    }

    private fun bitmapToBase64(bitmap: Bitmap): String {
        val out = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 90, out)
        return Base64.encodeToString(out.toByteArray(), Base64.NO_WRAP)
    }

    // ---------- 工具 ----------

    private val packageNameOfSelf: String get() = packageName

    private fun appNameOf(packageName: String): String {
        return try {
            val pm = packageManager
            val info = pm.getApplicationInfo(packageName, 0)
            info.loadLabel(pm).toString()
        } catch (e: PackageManager.NameNotFoundException) {
            packageName
        }
    }

    companion object {
        private const val TAG = "MsgBridge/Notif"

        /** 延迟发送窗口：50ms 内被移除的通知不推送（与参考一致） */
        private const val NOTIFICATION_SYNC_DELAY_MS = 50L

        /** 系统绑定/解绑时维护的当前实例，供配对成功后同步存量通知 */
        var instance: NotificationListener? = null
            private set

        /** 检查通知监听权限是否已授予 */
        fun hasReadNotificationsPermission(context: Context): Boolean {
            val cn = ComponentName(context, NotificationListener::class.java)
            val flat = Settings.Secure.getString(
                context.contentResolver,
                "enabled_notification_listeners"
            ) ?: return false
            return flat.split(":").contains(cn.flattenToString())
        }
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
    }

    override fun onDestroy() {
        super.onDestroy()
        mainHandler.removeCallbacksAndMessages(null)
        pendingTasks.clear()
        if (instance === this) {
            instance = null
        }
    }
}
