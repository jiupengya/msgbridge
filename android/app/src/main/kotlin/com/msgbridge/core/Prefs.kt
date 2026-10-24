package com.msgbridge.core

import android.content.Context
import android.content.SharedPreferences

/**
 * 轻量偏好存储（SharedPreferences 封装）
 */
object Prefs {
    private const val NAME = "msgbridge"

    lateinit var sp: SharedPreferences
        private set

    fun init(context: Context) {
        sp = context.applicationContext.getSharedPreferences(NAME, Context.MODE_PRIVATE)
    }

    /** 电脑 IP 地址 */
    var host: String
        get() = sp.getString("host", "") ?: ""
        set(value) = sp.edit().putString("host", value).apply()

    /** 电脑端口 */
    var port: Int
        get() = sp.getInt("port", 17001)
        set(value) = sp.edit().putInt("port", value).apply()

    /** 推送服务总开关 */
    var serviceEnabled: Boolean
        get() = sp.getBoolean("service_enabled", false)
        set(value) = sp.edit().putBoolean("service_enabled", value).apply()

    /** 通知推送开关 */
    var pushNotifications: Boolean
        get() = sp.getBoolean("push_notifications", true)
        set(value) = sp.edit().putBoolean("push_notifications", value).apply()

    /** 仅锁屏时推送通知（参考 KDE Connect 的 sendOnlyIfLocked） */
    var pushOnlyWhenLocked: Boolean
        get() = sp.getBoolean("push_only_when_locked", false)
        set(value) = sp.edit().putBoolean("push_only_when_locked", value).apply()

    /** 是否已与电脑完成配对 */
    var paired: Boolean
        get() = sp.getBoolean("paired", false)
        set(value) = sp.edit().putBoolean("paired", value).apply()

    /** 最近一次连接成功的时间（毫秒），0 表示从未连上 */
    var lastConnectedAt: Long
        get() = sp.getLong("last_connected_at", 0L)
        set(value) = sp.edit().putLong("last_connected_at", value).apply()

    /** 配对设备的 deviceId（用于展示） */
    var pairedDeviceId: String
        get() = sp.getString("paired_device_id", "") ?: ""
        set(value) = sp.edit().putString("paired_device_id", value).apply()
}
