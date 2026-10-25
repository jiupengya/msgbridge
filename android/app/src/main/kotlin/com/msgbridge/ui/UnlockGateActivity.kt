package com.msgbridge.ui

import android.app.Activity
import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.util.Log
import com.msgbridge.core.BridgeController

/**
 * 锁屏时点通知的中转页。
 *
 * 页面本身是透明的，什么都不画 —— 它只做三件事：
 *  1. `showWhenLocked` + `turnScreenOn` 让页面站到锁屏上面，顺便点亮屏幕
 *  2. `requestDismissKeyguard()` 把系统的解锁界面（密码 / 图案 / PIN）直接弹出来，
 *     省掉「亮屏 → 上滑 → 输密码」里的上滑那步
 *  3. 解锁通过后把控制权交回 [BridgeController]，由它启动真正要显示的界面
 *
 * 解锁成功后我们是前台，这时候启动别人的 Activity 不受「后台启动」限制，
 * 所以不需要叠加窗口那一套。
 */
class UnlockGateActivity : Activity() {

    /** 防止 requestDismissKeyguard 回调和 onResume 双双触发。 */
    private var handedOver = false

    /** 这一轮请求的序号，和 [BridgeController] 里存的对照。 */
    private var generation = 0

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        }
        generation = intent?.getIntExtra(BridgeController.EXTRA_UNLOCK_GENERATION, 0) ?: 0
    }

    override fun onResume() {
        super.onResume()
        if (handedOver) return
        // 放在 onResume 而不是 onCreate：MIUI 上页面还没真正可见时调
        // requestDismissKeyguard() 可能根本不回调，表现就是「亮屏了但什么都不发生」。
        // 就算在 onResume 里第一次也可能被忽略，所以下一帧再补一次。
        requestDismiss()
        window.decorView.postDelayed({
            if (!handedOver) requestDismiss()
        }, RETRY_DELAY_MS)
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        // 用户在等解锁时又点了一条：换成新的请求序号，重新请求一次
        if (handedOver) return
        generation = intent?.getIntExtra(BridgeController.EXTRA_UNLOCK_GENERATION, generation)
            ?: generation
        requestDismiss()
    }

    override fun onDestroy() {
        super.onDestroy()
        // 只有「没走完流程」才作废请求。正常 handOver 之后 finish() 的 onDestroy
        // 可能比下一次点击还晚到，代码里靠序号（generation）挡住误清。
        if (!handedOver) BridgeController.onUnlockGateDismissed(generation)
    }

    private fun requestDismiss() {
        val keyguard = getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
        if (!keyguard.isKeyguardLocked) {
            handOver()
            return
        }

        Log.i(TAG, "request dismiss keyguard (gen=$generation)")
        keyguard.requestDismissKeyguard(this, object : KeyguardManager.KeyguardDismissCallback() {
            override fun onDismissSucceeded() = handOver()
            override fun onDismissCancelled() = cancel()
            override fun onDismissError() {
                // 个别 ROM 直接报错：留在页面上，下次回到前台会再试一次
                Log.w(TAG, "dismiss keyguard error (gen=$generation)")
            }
        })
    }

    private fun handOver() {
        if (handedOver) return
        handedOver = true
        Log.i(TAG, "unlocked, handing over (gen=$generation)")
        BridgeController.onUnlockGatePassed(generation)
        finish()
    }

    private fun cancel() {
        if (handedOver) return
        handedOver = true
        Log.i(TAG, "unlock cancelled (gen=$generation)")
        BridgeController.onUnlockGateCancelled(generation)
        finish()
    }

    private companion object {
        const val TAG = "MsgBridge/UnlockGate"

        /** 补一次 requestDismissKeyguard 的延迟（等窗口真正可见）。 */
        const val RETRY_DELAY_MS = 200L
    }
}
