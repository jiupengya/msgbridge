package com.msgbridge.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Warning
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import com.msgbridge.core.BridgeController
import com.msgbridge.core.BridgeProtocol
import com.msgbridge.core.ConnectionState
import com.msgbridge.core.Prefs
import com.msgbridge.core.ShizukuHelper
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.Surface
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.theme.MiuixTheme
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * 主页：连接状态 + 常用开关 + 蓝牙兜底状态。
 *
 * 权限、使用说明这些「配一次就很少动」的东西都挪到 [SettingsScreen] 了，
 * 主页只留一眼能看完的内容。
 */
@Composable
fun HomeScreen(onOpenSettings: () -> Unit) {
    val context = LocalContext.current
    val state by BridgeController.state.collectAsState()
    val peerName by BridgeController.peerName.collectAsState()

    Surface(
        modifier = Modifier.fillMaxSize(),
        color = MiuixTheme.colorScheme.background
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .statusBarsPadding()
                .navigationBarsPadding()
                .verticalScroll(rememberScrollState())
                .padding(bottom = 16.dp),
        ) {
            PageTitle(
                title = "Message",
                subtitle = "手机通知推送到 Windows",
                action = {
                    HeaderIconButton(onClick = onOpenSettings) {
                        Icon(
                            imageVector = Icons.Filled.Settings,
                            contentDescription = "设置",
                            tint = MiuixTheme.colorScheme.onBackgroundVariant
                        )
                    }
                }
            )

            Column(Modifier.padding(horizontal = 16.dp)) {
                VSpace(12)

                // ---- 连接状态 ----
                ConnectionCard(state, peerName)

                if (state == ConnectionState.AwaitingPair) {
                    VSpace(12)
                    PairCard()
                }

                VSpace(12)
                SwitchesCard()

                VSpace(12)
                WifiFallbackCard()

                VSpace(12)
                Spacer(Modifier.height(4.dp))
                Text(
                    text = "实现方式参考 KDE Connect：JSON 行协议 + TCP 长连接。",
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
        }
    }
}

// ---------------- 连接状态 ----------------

@Composable
private fun ConnectionCard(state: ConnectionState, peerName: String) {
    val host = Prefs.host
    val port = Prefs.port
    val target = if (host.isBlank()) "未设置电脑地址" else "$host:$port"
    val lastConnected = formatLastConnected(Prefs.lastConnectedAt)

    val (title, subtitle, tint) = when (state) {
        ConnectionState.Connected -> Triple(
            "已连接 · $peerName",
            "$target · 最近联通 $lastConnected",
            MiuixTheme.colorScheme.primary
        )
        ConnectionState.AwaitingPair -> Triple(
            "等待配对",
            "$target · 在电脑端查看 PIN 并输入",
            MiuixTheme.colorScheme.error
        )
        ConnectionState.Connecting -> Triple(
            "正在连接…",
            "目标 $target",
            MiuixTheme.colorScheme.onBackgroundVariant
        )
        ConnectionState.Disconnected -> Triple(
            "未连接",
            "$target · 最近联通 $lastConnected",
            MiuixTheme.colorScheme.onBackgroundVariant
        )
    }
    val icon = when (state) {
        ConnectionState.Connected -> Icons.Filled.Check
        ConnectionState.AwaitingPair -> Icons.Filled.Warning
        ConnectionState.Connecting -> Icons.Filled.Refresh
        ConnectionState.Disconnected -> Icons.Filled.Info
    }

    SectionCard {
        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(imageVector = icon, contentDescription = null, tint = tint)
            Spacer(Modifier.width(12.dp))
            Column {
                Text(title, style = MiuixTheme.textStyles.body1, color = tint)
                Spacer(Modifier.height(2.dp))
                Text(
                    subtitle,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
        }
    }
}

@Composable
private fun PairCard() {
    var pin by remember { mutableStateOf("") }

    SectionCard(title = "配对") {
        Text(
            text = "电脑端已弹窗显示 6 位 PIN 码，输入后即可完成配对。若电脑端未弹窗，请先在电脑上打开「Message」窗口。",
            style = MiuixTheme.textStyles.body2,
            color = MiuixTheme.colorScheme.onBackgroundVariant
        )
        VSpace(12)
        TextField(
            value = pin,
            onValueChange = { pin = it.filter(Char::isDigit).take(6) },
            modifier = Modifier.fillMaxWidth(),
            label = "PIN 码",
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number)
        )
        VSpace(12)
        Button(
            onClick = {
                if (pin.length == 6) {
                    BridgeController.confirmPin(pin)
                    pin = ""
                }
            },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text("确认配对")
        }
        VSpace(8)
        top.yukonga.miuix.kmp.basic.TextButton(
            text = "重新发起配对请求",
            onClick = { BridgeController.retryPair() },
            modifier = Modifier.fillMaxWidth()
        )
    }
}

// ---------------- 推送开关 ----------------

@Composable
private fun SwitchesCard() {
    val context = LocalContext.current
    val serviceEnabled by BridgeController.serviceEnabled.collectAsState()
    val connectionState by BridgeController.state.collectAsState()
    val windowsToast by BridgeController.windowsToast.collectAsState()
    val dndFilter by BridgeController.interruptionFilter.collectAsState()
    val canSetDnd by BridgeController.canSetDnd.collectAsState()
    var pushNotifications by remember { mutableStateOf(Prefs.pushNotifications) }
    var pushOnlyWhenLocked by remember { mutableStateOf(Prefs.pushOnlyWhenLocked) }

    val connected = connectionState == ConnectionState.Connected

    SectionCard(title = "推送") {
        SwitchRow(
            title = "推送服务",
            subtitle = "开启后常驻后台并自动连接电脑",
            checked = serviceEnabled,
            onCheckedChange = { BridgeController.setServiceEnabled(context, it) }
        )
        SwitchRow(
            title = "通知推送",
            subtitle = "将手机应用通知推送到电脑",
            checked = pushNotifications,
            onCheckedChange = { v ->
                pushNotifications = v
                Prefs.pushNotifications = v
            }
        )
        SwitchRow(
            title = "仅锁屏推送",
            subtitle = "仅当手机锁屏时推送通知",
            checked = pushOnlyWhenLocked,
            onCheckedChange = { v ->
                pushOnlyWhenLocked = v
                Prefs.pushOnlyWhenLocked = v
            }
        )
        SwitchRow(
            title = "电脑系统通知（气泡）",
            subtitle = if (connected) "关闭后电脑只记录消息、不弹通知"
            else "未连接电脑，暂时无法切换",
            checked = windowsToast,
            enabled = connected,
            onCheckedChange = { BridgeController.requestWindowsToast(it) }
        )
        SwitchRow(
            title = "手机勿扰",
            subtitle = when {
                !canSetDnd -> "需要「勿扰访问权限」，到设置里授予"
                dndFilter == BridgeProtocol.FILTER_DND -> "已开启勿扰，通知仍会同步到电脑"
                else -> "开启后手机不再响铃，通知仍会同步到电脑"
            },
            checked = dndFilter == BridgeProtocol.FILTER_DND,
            enabled = canSetDnd,
            onCheckedChange = { on ->
                BridgeController.setInterruptionFilter(
                    if (on) BridgeProtocol.FILTER_DND else BridgeProtocol.FILTER_ALL
                )
            }
        )
    }
}

// ---------------- 蓝牙兜底 ----------------

@Composable
private fun WifiFallbackCard() {
    val context = LocalContext.current
    val fallbackEnabled = remember { mutableStateOf(Prefs.wifiFallbackEnabled) }
    val shizukuReady = remember { ShizukuHelper.hasPermission() }
    val target = remember { Prefs.targetSsid }
    var testing by remember { mutableStateOf(false) }
    var testResult by remember { mutableStateOf<String?>(null) }

    SectionCard(title = "WiFi 兜底") {
        SwitchRow(
            title = "允许电脑经蓝牙打开 WiFi",
            subtitle = "手机忘了开 WiFi 或连错网络时，可在电脑托盘里一键拉回来",
            checked = fallbackEnabled.value,
            onCheckedChange = {
                fallbackEnabled.value = it
                Prefs.wifiFallbackEnabled = it
            }
        )

        Row(
            modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text("目标网络", style = MiuixTheme.textStyles.body1)
                Text(
                    target,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
            Text(
                text = when {
                    !fallbackEnabled.value -> "已关闭"
                    shizukuReady -> "Shizuku 已就绪"
                    else -> "需要 Shizuku 授权"
                },
                style = MiuixTheme.textStyles.body2,
                color = if (shizukuReady) MiuixTheme.colorScheme.primary
                else MiuixTheme.colorScheme.error
            )
        }

        VSpace(4)
        Button(
            onClick = {
                if (testing || !shizukuReady) return@Button
                testing = true
                testResult = null
                BridgeController.ensureTargetWifi { msg ->
                    testing = false
                    testResult = msg
                }
            },
            modifier = Modifier.fillMaxWidth()
        ) {
            Text(
                if (testing) "正在连接…"
                else if (!shizukuReady) "先在设置里授权 Shizuku"
                else "现在就让手机连回 $target"
            )
        }

        testResult?.let {
            VSpace(6)
            Text(
                it,
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
    }
}

// ---------------- 时间格式化 ----------------

private fun formatLastConnected(ms: Long): String {
    if (ms <= 0L) return "从未"
    return SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(Date(ms))
}
