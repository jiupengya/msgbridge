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

    // ---------------- WiFi 兜底 ----------------

    /** 电脑通过蓝牙要求手机连的 WiFi 名 */
    var targetSsid: String
        get() = sp.getString("target_ssid", "jiupengya-5G") ?: "jiupengya-5G"
        set(value) = sp.edit().putString("target_ssid", value).apply()

    /**
     * 目标 WiFi 的密码，**可选**。
     * 留空时只走「关→开，让系统自己连已保存网络」，通常就够；
     * 填了的话在系统没连上时还能用 connect-network 精确指定。
     */
    var targetWifiPassword: String
        get() = sp.getString("target_wifi_password", "") ?: ""
        set(value) = sp.edit().putString("target_wifi_password", value).apply()

    /** 是否允许电脑经蓝牙让手机打开 / 切换 WiFi */
    var wifiFallbackEnabled: Boolean
        get() = sp.getBoolean("wifi_fallback_enabled", true)
        set(value) = sp.edit().putBoolean("wifi_fallback_enabled", value).apply()
}
