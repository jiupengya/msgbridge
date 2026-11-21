package com.msgbridge.core

import android.annotation.SuppressLint
import android.content.pm.PackageManager
import android.util.Log
import rikka.shizuku.Shizuku
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Shizuku 桥：借 shell 身份执行命令。
 *
 * 为什么需要它：Android 10 起普通应用调用 `WifiManager.setWifiEnabled()` 不再生效
 * （API 29 废弃，targetSdk ≥ 29 直接无效）。而 `cmd wifi set-wifi-enabled` /
 * `connect-network` 这些命令对 shell 是放行的，Shizuku 正好能把我们的进程
 * 以 shell 身份派生出去。
 */
object ShizukuHelper {

    private const val TAG = "MsgBridge/Shizuku"

    /** 申请权限用的 requestCode，和 Activity 里那个必须一致 */
    const val REQUEST_CODE = 0x5B1F

    data class ShellResult(val code: Int, val stdout: String, val stderr: String) {
        val ok: Boolean get() = code == 0
    }

    /** Shizuku 服务端是否在跑（用户装了 Shizuku 且已启动） */
    fun isBinderAlive(): Boolean = try {
        Shizuku.pingBinder()
    } catch (e: Throwable) {
        false
    }

    /** 我们是否已经拿到 Shizuku 授权 */
    fun hasPermission(): Boolean = try {
        isBinderAlive() && Shizuku.checkSelfPermission() == PackageManager.PERMISSION_GRANTED
    } catch (e: Throwable) {
        false
    }

    /** 弹出 Shizuku 授权框（必须在 Activity 存活时调用） */
    fun requestPermission() {
        if (!isBinderAlive()) return
        try {
            Shizuku.requestPermission(REQUEST_CODE)
        } catch (e: Throwable) {
            Log.w(TAG, "requestPermission failed: ${e.message}")
        }
    }

    /**
     * `Shizuku.newProcess()` 是 @RestrictTo 但运行时存在的公开静态方法，
     * 反射拿到即可；没有它就只能走 binder 调各种系统服务，代价大得多。
     */
    @Suppress("PrivateApi")
    private val newProcessMethod by lazy {
        runCatching {
            Shizuku::class.java
                .getDeclaredMethod(
                    "newProcess",
                    Array<String>::class.java,
                    Array<String>::class.java,
                    String::class.java
                )
                .apply { isAccessible = true }
        }.onFailure { Log.w(TAG, "newProcess unavailable: ${it.message}") }.getOrNull()
    }

    /** 同步执行一条 shell 命令。调用方负责放到 IO 线程。 */
    fun exec(command: String, timeoutMs: Long = 20_000L): ShellResult {
        if (!hasPermission()) return ShellResult(-1, "", "shizuku permission not granted")
        val method = newProcessMethod
            ?: return ShellResult(-1, "", "shizuku newProcess unavailable")

        return try {
            val process = method.invoke(
                null,
                arrayOf("sh", "-c", command),
                null,
                null
            ) as Process

            // 坑 1：Shizuku 的 exitValue() 走 binder，进程没结束时服务端抛的是
            // IllegalArgumentException（不是 JDK 约定的 IllegalThreadStateException），
            // 所以 Process.waitFor(timeout) 那套默认实现会直接炸。
            // 坑 2：exitValue() 在命令运行期间还可能把调用方阻塞在 binder 上，
            // 光轮询它会卡死。这里改成「stdout 读完 = 命令结束」，一个 binder 调用都不做。
            val outBuf = StringBuilder()
            val errBuf = StringBuilder()
            val done = CountDownLatch(1)

            Thread {
                runCatching {
                    process.inputStream.bufferedReader().use { outBuf.append(it.readText()) }
                }
                done.countDown()
            }.apply { isDaemon = true }.start()

            Thread {
                runCatching {
                    process.errorStream.bufferedReader().use { errBuf.append(it.readText()) }
                }
            }.apply { isDaemon = true }.start()

            if (!done.await(timeoutMs, TimeUnit.MILLISECONDS)) {
                runCatching { process.destroy() }
                return ShellResult(-1, outBuf.toString(), "timeout after ${timeoutMs}ms")
            }

            val out = outBuf.toString()
            Log.i(TAG, "exec ${command.take(80)} -> ${out.trim().take(120)}")
            ShellResult(0, out, errBuf.toString())
        } catch (e: Throwable) {
            Log.w(TAG, "exec failed: ${e.message}")
            ShellResult(-1, "", e.message ?: e.javaClass.simpleName)
        }
    }
}
