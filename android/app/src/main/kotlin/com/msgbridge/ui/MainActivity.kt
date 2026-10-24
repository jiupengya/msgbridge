package com.msgbridge.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import com.msgbridge.core.BridgeController
import com.msgbridge.core.Prefs
import com.msgbridge.service.BridgeService

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 开关已开启（或上次重装前开启）时自动拉起前台服务，
        // 否则用户会误以为"开了推送服务却没连上"。
        if (Prefs.serviceEnabled) {
            BridgeService.start(this)
        }
        setContent {
            MessageBridgeTheme {
                HomeScreen()
            }
        }
    }

    override fun onResume() {
        super.onResume()
        // 从系统设置页回来时刷新勿扰状态与权限（用户可能刚授予「勿扰访问权限」）
        BridgeController.refreshDndState()
        if (BridgeController.isConnected()) {
            BridgeController.reportInterruptionState()
        }
    }
}
