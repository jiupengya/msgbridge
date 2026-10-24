package com.msgbridge.service

import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import com.msgbridge.R
import com.msgbridge.core.BridgeController
import com.msgbridge.core.Prefs

/**
 * 控制中心（下拉通知栏）快捷开关：一键开关推送服务。
 * 状态与 App 内「推送服务」开关共用 BridgeController.serviceEnabled，两边永远一致。
 */
class BridgeTileService : TileService() {

    override fun onStartListening() {
        super.onStartListening()
        updateTile()
    }

    override fun onClick() {
        super.onClick()
        // 点击时切换：开 <-> 关
        BridgeController.setServiceEnabled(this, !Prefs.serviceEnabled)
        updateTile()
    }

    private fun updateTile() {
        val tile = qsTile ?: return
        val enabled = Prefs.serviceEnabled

        tile.state = if (enabled) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        tile.label = getString(R.string.app_name)
        tile.contentDescription = getString(R.string.tile_description)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = when {
                !enabled -> "已关闭"
                BridgeController.isConnected() -> "已连接"
                else -> "未连接"
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            tile.stateDescription = if (enabled) "推送服务运行中" else "推送服务已关闭"
        }
        tile.updateTile()
    }
}
