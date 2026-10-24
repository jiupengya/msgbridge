package com.msgbridge.ui

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
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
}
