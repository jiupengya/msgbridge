package com.msgbridge.core

import android.os.SystemClock
import android.util.Log
import kotlinx.coroutines.delay

/**
 * 手机 WiFi 控制（依赖 Shizuku 的 shell 身份）。
 *
 * 普通应用在 Android 10+ 拿不到开关 WiFi 的权限，只有 `cmd wifi` 系列命令
 * 对 shell 放行。这里把「打开 WiFi / 切到指定网络」封装成一个幂等动作：
 * 先看现状，已经对了就什么都不做。
 */
object WifiControl {

    private const val TAG = "MsgBridge/Wifi"

    data class Status(val enabled: Boolean, val ssid: String, val raw: String) {
        val connected: Boolean get() = ssid.isNotEmpty()
    }

    enum class Outcome {
        /** 本来就连着目标网络 */
        ALREADY_OK,

        /** 之前 WiFi 是关的，打开后自己连上了 */
        ENABLED,

        /** 之前连错了，关→开之后连回目标 */
        SWITCHED,

        /** 关→开没连上，用 connect-network 显式连的 */
        CONNECTED,

        /** 都试了还是没连上 */
        FAILED,
    }

    data class Result(
        val outcome: Outcome,
        val ssid: String,
        val message: String,
        val elapsedMs: Long,
    ) {
        val ok: Boolean get() = outcome != Outcome.FAILED
    }

    // ---------------- 基础操作 ----------------

    fun status(): Status {
        val r = ShizukuHelper.exec("cmd wifi status")
        val raw = r.stdout
        val enabled = raw.contains("Wifi is enabled")
        // 有多条 ClientModeManager 时取第一个 "connected to"
        val ssid = Regex("""Wifi is connected to "(.*?)"""")
            .find(raw)?.groupValues?.get(1).orEmpty()
        return Status(enabled, ssid, raw)
    }

    fun setEnabled(enabled: Boolean): Boolean {
        val r = ShizukuHelper.exec("cmd wifi set-wifi-enabled ${if (enabled) "enabled" else "disabled"}")
        return r.code == 0
    }

    fun connect(ssid: String, password: String): Boolean {
        val r = ShizukuHelper.exec(
            "cmd wifi connect-network ${shellQuote(ssid)} wpa2 ${shellQuote(password)}"
        )
        return r.code == 0
    }

    // ---------------- 幂等主流程 ----------------

    /**
     * 确保手机连上 [target]。
     *
     * 策略（对「忘了开 WiFi」和「连错 WiFi」都覆盖）：
     *  1. 已经连着 → 直接返回
     *  2. WiFi 关着 → 打开，等系统自己连（家里这个网络是保存过的，通常 1~2 秒就回来）
     *  3. 开着但连错 → 关再开，让系统重新选网
     *  4. 还没好且给了密码 → `connect-network` 显式指定
     */
    suspend fun ensureOnTarget(target: String, password: String?): Result {
        val begin = SystemClock.elapsedRealtime()
        fun elapsed() = SystemClock.elapsedRealtime() - begin

        if (target.isBlank()) {
            return Result(Outcome.FAILED, "", "未设置目标 WiFi", elapsed())
        }

        var st = status()
        Log.i(TAG, "before: enabled=${st.enabled} ssid=${st.ssid} target=$target")
        if (st.enabled && st.ssid == target) {
            return Result(Outcome.ALREADY_OK, st.ssid, "已连接 $target", elapsed())
        }

        if (!st.enabled) {
            setEnabled(true)
            waitFor(target, 10_000)?.let {
                return Result(Outcome.ENABLED, it.ssid, "已打开 WiFi 并连上 $target", elapsed())
            }
        }

        // 上面没成功：关→开，重新让系统选网
        setEnabled(false)
        delay(1_200)
        setEnabled(true)
        waitFor(target, 15_000)?.let {
            return Result(Outcome.SWITCHED, it.ssid, "已切回 $target", elapsed())
        }

        // 还是不对，用密码显式连一次
        if (!password.isNullOrBlank()) {
            connect(target, password)
            waitFor(target, 10_000)?.let {
                return Result(Outcome.CONNECTED, it.ssid, "已连接到 $target", elapsed())
            }
        }

        st = status()
        return Result(
            Outcome.FAILED,
            st.ssid,
            if (st.ssid.isEmpty()) "没能连上 $target（当前无 WiFi）"
            else "没能连上 $target（当前是 ${st.ssid}）",
            elapsed()
        )
    }

    /** 等手机连上 [target]，超时返回 null */
    private suspend fun waitFor(target: String, timeoutMs: Long): Status? {
        val deadline = SystemClock.elapsedRealtime() + timeoutMs
        while (SystemClock.elapsedRealtime() < deadline) {
            delay(500)
            val s = status()
            if (s.enabled && s.ssid == target) return s
        }
        return null
    }

    /** 单引号包住参数，内部单引号转义成 '\'' ；SSID/密码里出现引号也不至于拆错 */
    private fun shellQuote(s: String): String =
        "'" + s.replace("'", "'\\''") + "'"
}
