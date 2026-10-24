package com.msgbridge.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.msgbridge.core.Prefs

/**
 * 开机 / 应用升级后自启推送服务（仅当服务开关开启）。
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action ?: return
        if (action == Intent.ACTION_BOOT_COMPLETED || action == Intent.ACTION_MY_PACKAGE_REPLACED) {
            if (Prefs.serviceEnabled) {
                BridgeService.start(context)
            }
        }
    }
}
