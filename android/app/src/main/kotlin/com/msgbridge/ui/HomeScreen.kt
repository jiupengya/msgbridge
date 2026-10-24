package com.msgbridge.ui

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
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
import androidx.core.content.ContextCompat
import com.msgbridge.core.BridgeController
import com.msgbridge.core.BridgeProtocol
import com.msgbridge.core.ConnectionState
import com.msgbridge.core.Prefs
import com.msgbridge.service.NotificationListener
import kotlinx.coroutines.flow.StateFlow
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.Surface
import top.yukonga.miuix.kmp.basic.Switch
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.theme.MiuixTheme

@Composable
fun HomeScreen() {
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
                .padding(horizontal = 16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            Spacer(Modifier.height(8.dp))
            Text(
                text = "Message",
                style = MiuixTheme.textStyles.headline1
            )
            Text(
                text = "将安卓手机的通知与短信推送到 Windows 电脑",
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )

            // ---- 连接状态卡片 ----
            ConnectionCard(state, peerName)
            if (state == ConnectionState.AwaitingPair) {
                PairCard()
            }

            // ---- 连接设置 ----
            ConnectionSettingsCard()

            // ---- 推送开关 ----
            SwitchesCard(context)

            // ---- 权限 ----
            PermissionsCard(context)

            // ---- 说明 ----
            AboutCard()

            Spacer(Modifier.height(16.dp))
        }
    }
}

// ---------------- 连接状态卡片 ----------------

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

    Card {
        Row(
            modifier = Modifier.fillMaxWidth().padding(16.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(imageVector = icon, contentDescription = null, tint = tint)
            Spacer(Modifier.width(12.dp))
            Column {
                Text(
                    text = title,
                    style = MiuixTheme.textStyles.body1,
                    color = tint
                )
                Spacer(Modifier.height(2.dp))
                Text(
                    text = subtitle,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
        }
    }
}

// ---------------- 配对卡片 ----------------

@Composable
private fun PairCard() {
    var pin by remember { mutableStateOf("") }

    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Text(
                text = "配对请求",
                style = MiuixTheme.textStyles.body1
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = "电脑端已弹窗显示 6 位 PIN 码，输入后即可完成配对。若电脑端未弹窗，请先在电脑上打开「Message」窗口。",
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
            Spacer(Modifier.height(12.dp))
            TextField(
                value = pin,
                onValueChange = { pin = it.filter(Char::isDigit).take(6) },
                modifier = Modifier.fillMaxWidth(),
                label = "PIN 码",
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number)
            )
            Spacer(Modifier.height(12.dp))
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
            Spacer(Modifier.height(8.dp))
            TextButton(
                text = "重新发起配对请求",
                onClick = { BridgeController.retryPair() },
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}

// ---------------- 连接设置卡片 ----------------

@Composable
private fun ConnectionSettingsCard() {
    val context = LocalContext.current
    var host by remember { mutableStateOf(Prefs.host) }
    var port by remember { mutableStateOf(Prefs.port.toString()) }

    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Text("连接设置", style = MiuixTheme.textStyles.body1)
            Spacer(Modifier.height(12.dp))
            TextField(
                value = host,
                onValueChange = {
                    host = it
                    Prefs.host = it
                },
                modifier = Modifier.fillMaxWidth(),
                label = "电脑 IP 地址（局域网内）",
                singleLine = true
            )
            Spacer(Modifier.height(8.dp))
            TextField(
                value = port,
                onValueChange = {
                    if (it.all(Char::isDigit) && it.length <= 5) {
                        port = it
                        it.toIntOrNull()?.let { v -> if (v in 1..65535) Prefs.port = v }
                    }
                },
                modifier = Modifier.fillMaxWidth(),
                label = "端口（与电脑端一致，默认 17001）",
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                singleLine = true
            )
            Spacer(Modifier.height(12.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Button(
                    onClick = { BridgeController.restart() },
                    modifier = Modifier.weight(1f)
                ) {
                    Text("保存并重连")
                }
                TextButton(
                    text = "重置配对",
                    onClick = {
                        Prefs.paired = false
                        Prefs.pairedDeviceId = ""
                        BridgeController.restart()
                    },
                    modifier = Modifier.weight(1f)
                )
            }
            Spacer(Modifier.height(8.dp))
            // 彻底从最近任务列表移除界面；前台服务与连接不受影响
            TextButton(
                text = "隐藏到后台（从最近任务移除）",
                onClick = { (context as? Activity)?.finishAndRemoveTask() },
                modifier = Modifier.fillMaxWidth()
            )
        }
    }
}

// ---------------- 推送开关卡片 ----------------

@Composable
private fun SwitchesCard(context: Context) {
    val serviceEnabled by BridgeController.serviceEnabled.collectAsState()
    val connectionState by BridgeController.state.collectAsState()
    val windowsToast by BridgeController.windowsToast.collectAsState()
    val dndFilter by BridgeController.interruptionFilter.collectAsState()
    val canSetDnd by BridgeController.canSetDnd.collectAsState()
    var pushNotifications by remember { mutableStateOf(Prefs.pushNotifications) }
    var pushOnlyWhenLocked by remember { mutableStateOf(Prefs.pushOnlyWhenLocked) }

    val connected = connectionState == ConnectionState.Connected

    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Text("推送开关", style = MiuixTheme.textStyles.body1)

            SwitchRow(
                title = "推送服务",
                subtitle = "开启后常驻后台并自动连接电脑",
                checked = serviceEnabled,
                onCheckedChange = { enabled ->
                    BridgeController.setServiceEnabled(context, enabled)
                }
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

            Spacer(Modifier.height(12.dp))
            Text("系统开关", style = MiuixTheme.textStyles.body1)

            // 电脑是否弹系统通知气泡：状态由电脑下发，点击后把指令发给电脑
            SwitchRow(
                title = "电脑系统通知（气泡）",
                subtitle = if (connected) "关闭后电脑只记录消息、不弹通知"
                else "未连接电脑，暂时无法切换",
                checked = windowsToast,
                enabled = connected,
                onCheckedChange = { BridgeController.requestWindowsToast(it) }
            )
            // 手机本机勿扰：不是「暂停推送」，只是让手机不再响铃
            SwitchRow(
                title = "手机勿扰",
                subtitle = when {
                    !canSetDnd -> "需要「勿扰访问权限」，请到下方权限处授予"
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
}

@Composable
private fun SwitchRow(
    title: String,
    subtitle: String,
    checked: Boolean,
    enabled: Boolean = true,
    onCheckedChange: (Boolean) -> Unit
) {
    Row(
        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(
                title,
                style = MiuixTheme.textStyles.body1,
                color = if (enabled) MiuixTheme.colorScheme.onBackground
                else MiuixTheme.colorScheme.onBackgroundVariant
            )
            Text(
                subtitle,
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
        Spacer(Modifier.width(8.dp))
        Switch(
            checked = checked,
            onCheckedChange = { if (enabled) onCheckedChange(it) },
            enabled = enabled
        )
    }
}

// ---------------- 权限卡片 ----------------

@Composable
private fun PermissionsCard(context: Context) {
    var permissionTick by remember { mutableStateOf(0) }

    val canSetDnd by BridgeController.canSetDnd.collectAsState()

    val hasNotifListener = NotificationListener.hasReadNotificationsPermission(context)
    val hasPostNotif = Build.VERSION.SDK_INT < 33 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
        PackageManager.PERMISSION_GRANTED

    // 权限变更后刷新
    remember(permissionTick) {
        // 无操作，仅用于触发重组
    }

    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Text("权限", style = MiuixTheme.textStyles.body1)
            Spacer(Modifier.height(4.dp))

            PermissionRow(
                title = "通知使用权",
                subtitle = "监听手机通知，推送到电脑",
                granted = hasNotifListener,
                onClick = {
                    context.startActivity(
                        Intent("android.settings.ACTION_NOTIFICATION_LISTENER_SETTINGS")
                    )
                }
            )
            if (Build.VERSION.SDK_INT >= 33) {
                PermissionRow(
                    title = "通知显示（Android 13+）",
                    subtitle = "显示后台运行状态通知",
                    granted = hasPostNotif,
                    onClick = {
                        context.startActivity(
                            Intent(
                                Settings.ACTION_APP_NOTIFICATION_SETTINGS
                            ).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                        )
                    }
                )
            }
            // 电脑远程开关手机勿扰需要这个权限
            PermissionRow(
                title = "勿扰访问权限",
                subtitle = "允许电脑远程开关手机勿扰",
                granted = canSetDnd,
                onClick = {
                    runCatching {
                        context.startActivity(Intent(Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS))
                    }
                }
            )
            PermissionRow(
                title = "忽略电池优化",
                subtitle = "熄屏后保持连接稳定（MIUI 还需把省电策略设为无限制）",
                granted = isIgnoringBatteryOptimizations(context),
                onClick = { requestIgnoreBatteryOptimizations(context) }
            )
        }
    }
}

@Composable
private fun PermissionRow(
    title: String,
    subtitle: String,
    granted: Boolean,
    onClick: () -> Unit
) {
    Surface(
        onClick = onClick,
        modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
        shape = RoundedCornerShape(16.dp),
        color = MiuixTheme.colorScheme.surfaceContainerHigh
    ) {
        Row(
            modifier = Modifier.fillMaxWidth().padding(12.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Icon(
                imageVector = if (granted) Icons.Filled.Check else Icons.Filled.Info,
                contentDescription = null,
                tint = if (granted) MiuixTheme.colorScheme.primary
                else MiuixTheme.colorScheme.onBackgroundVariant
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(title, style = MiuixTheme.textStyles.body1)
                Text(
                    subtitle,
                    style = MiuixTheme.textStyles.body2,
                    color = MiuixTheme.colorScheme.onBackgroundVariant
                )
            }
            Spacer(Modifier.width(8.dp))
            Text(
                text = if (granted) "已授权" else "去授权",
                style = MiuixTheme.textStyles.body2,
                color = if (granted) MiuixTheme.colorScheme.primary
                else MiuixTheme.colorScheme.error
            )
        }
    }
}

// ---------------- 说明卡片 ----------------

@Composable
private fun AboutCard() {
    Card {
        Column(Modifier.fillMaxWidth().padding(16.dp)) {
            Text("使用说明", style = MiuixTheme.textStyles.body1)
            Spacer(Modifier.height(4.dp))
            Text(
                text = "1. 电脑端运行「Message」并保持托盘常驻\n" +
                    "2. 手机填入电脑局域网 IP 与端口，开启推送服务\n" +
                    "3. 首次连接时在电脑弹窗查看 PIN，在手机输入完成配对\n" +
                    "4. 之后手机的通知会自动推送到电脑\n\n" +
                    "若一直无法连接：确认电脑端已运行，且 Windows 防火墙已放行 17001 端口。\n\n" +
                    "实现方式参考 KDE Connect：JSON 行协议 + TCP 长连接。",
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
    }
}

// ---------------- 通用组件 ----------------

@Composable
private fun Card(content: @Composable () -> Unit) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(16.dp),
        color = MiuixTheme.colorScheme.surface,
        shadowElevation = 2.dp
    ) {
        content()
    }
}

// ---------------- 时间格式化 ----------------

/** 最近一次连通时间；0 表示从未连上。 */
private fun formatLastConnected(ms: Long): String {
    if (ms <= 0L) return "从未"
    return SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(Date(ms))
}

// ---------------- 电池优化（参考 android-screen-off-survival） ----------------
private fun isIgnoringBatteryOptimizations(context: Context): Boolean {
    val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    return pm.isIgnoringBatteryOptimizations(context.packageName)
}

private fun requestIgnoreBatteryOptimizations(context: Context) {
    runCatching {
        context.startActivity(
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                .setData(android.net.Uri.fromParts("package", context.packageName, null))
        )
    }.onFailure {
        runCatching {
            context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }.onFailure {
            runCatching {
                context.startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                        .setData(android.net.Uri.fromParts("package", context.packageName, null))
                )
            }
        }
    }
}
