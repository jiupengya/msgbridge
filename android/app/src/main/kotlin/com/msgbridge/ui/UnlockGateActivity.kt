package com.msgbridge.ui

import android.app.Activity
import android.app.KeyguardManager
import android.content.Context
import android.os.Build
import android.os.Bundle
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

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O_MR1) {
            setShowWhenLocked(true)
            setTurnScreenOn(true)
        }

        val keyguard = getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
        if (!keyguard.isKeyguardLocked) {
            handOver()
            return
        }

        keyguard.requestDismissKeyguard(this, object : KeyguardManager.KeyguardDismissCallback() {
            override fun onDismissSucceeded() = handOver()
            override fun onDismissCancelled() = cancel()
            override fun onDismissError() = cancel()
        })
    }

    /**
     * 有些 ROM 上 requestDismissKeyguard 不回调（或者用户直接上滑解锁了），
     * 所以每次回到前台再检查一次，别让这次点击白点。
     */
    override fun onResume() {
        super.onResume()
        if (handedOver) return
        val keyguard = getSystemService(Context.KEYGUARD_SERVICE) as KeyguardManager
        if (!keyguard.isKeyguardLocked) handOver()
    }

    override fun onDestroy() {
        super.onDestroy()
        // 按返回键 / 被系统回收：这次打开请求作废
        BridgeController.onUnlockGateDismissed()
    }

    private fun handOver() {
        if (handedOver) return
        handedOver = true
        BridgeController.onUnlockGatePassed()
        finish()
    }

    private fun cancel() {
        if (handedOver) return
        handedOver = true
        BridgeController.onUnlockGateCancelled()
        finish()
    }
}
