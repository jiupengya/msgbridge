package com.msgbridge.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import com.msgbridge.core.BridgeController
import com.msgbridge.core.Prefs
import com.msgbridge.service.BridgeService

class MainActivity : ComponentActivity() {

    private val bluetoothPermissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) {
        // 授权结果不在这里处理：BridgeService 每次启动都会重新检查，
        // 授权后重启一次服务即可用上蓝牙通道。
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // 开关已开启（或上次重装前开启）时自动拉起前台服务，
        // 否则用户会误以为"开了推送服务却没连上"。
        if (Prefs.serviceEnabled) {
            BridgeService.start(this)
        }
        ensureBluetoothPermissions()
        setContent {
            MessageBridgeTheme {
                // 0 = 主页，1 = 设置页
                var page by rememberSaveable { mutableStateOf(0) }
                BackHandler(enabled = page != 0) { page = 0 }
                when (page) {
                    0 -> HomeScreen(onOpenSettings = { page = 1 })
                    else -> SettingsScreen(onBack = { page = 0 })
                }
            }
        }
    }

    override fun onResume() {
        super.onResume()
        // 从系统设置页回来时刷新勿扰状态与权限（用户可能刚授予「勿扰访问权限」/Shizuku）
        BridgeController.refreshDndState()
        BridgeController.refreshShizukuState()
        if (BridgeController.isConnected()) {
            BridgeController.reportInterruptionState()
        }
    }

    /**
     * Android 12 起蓝牙要运行时权限：
     *  - BLUETOOTH_ADVERTISE：广播 GATT 服务，让电脑能找到手机
     *  - BLUETOOTH_CONNECT：接受电脑的 GATT 连接
     */
    private fun ensureBluetoothPermissions() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return
        val needed = listOf(
            Manifest.permission.BLUETOOTH_CONNECT,
            Manifest.permission.BLUETOOTH_ADVERTISE,
        ).filter {
            ContextCompat.checkSelfPermission(this, it) != PackageManager.PERMISSION_GRANTED
        }
        if (needed.isNotEmpty()) {
            bluetoothPermissionLauncher.launch(needed.toTypedArray())
        }
    }
}
