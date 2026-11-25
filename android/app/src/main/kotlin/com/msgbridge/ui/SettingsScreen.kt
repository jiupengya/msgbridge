package com.msgbridge.ui

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
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
import com.msgbridge.core.Prefs
import com.msgbridge.core.ShizukuHelper
import com.msgbridge.service.NotificationListener
import top.yukonga.miuix.kmp.basic.Button
import top.yukonga.miuix.kmp.basic.Icon
import top.yukonga.miuix.kmp.basic.Surface
import top.yukonga.miuix.kmp.basic.Text
import top.yukonga.miuix.kmp.basic.TextButton
import top.yukonga.miuix.kmp.basic.TextField
import top.yukonga.miuix.kmp.theme.MiuixTheme

/**
 * 设置页：连接参数、权限、WiFi 兜底、使用说明。
 *
 * 这些都是一次性配好、之后很少动的东西，从主页搬出来之后主页只剩状态和开关。
 */
@Composable
fun SettingsScreen(onBack: () -> Unit) {
    val context = LocalContext.current

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
                .padding(bottom = 24.dp),
        ) {
            PageTitle(
                title = "设置",
                action = {
                    HeaderIconButton(onClick = onBack) {
                        Icon(
                            imageVector = Icons.Filled.ArrowBack,
                            contentDescription = "返回",
                            tint = MiuixTheme.colorScheme.onBackground
                        )
                    }
                }
            )

            Column(Modifier.padding(horizontal = 16.dp)) {
                VSpace(12)

                ConnectionSection(context)
                VSpace(16)

                PermissionSection(context)
                VSpace(16)

                WifiFallbackSection()
                VSpace(16)

                GuideSection()
                VSpace(16)

                AboutSection()
            }
        }
    }
}

// ---------------- 连接 ----------------

@Composable
private fun ConnectionSection(context: Context) {
    var host by remember { mutableStateOf(Prefs.host) }
    var port by remember { mutableStateOf(Prefs.port.toString()) }

    SectionCard(title = "连接") {
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
        VSpace(8)
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
        VSpace(12)
        Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
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
        VSpace(4)
        // 彻底从最近任务列表移除界面；前台服务与连接不受影响
        TextButton(
            text = "隐藏到后台（从最近任务移除）",
            onClick = { (context as? Activity)?.finishAndRemoveTask() },
            modifier = Modifier.fillMaxWidth()
        )
    }
}

// ---------------- 权限 ----------------

@Composable
private fun PermissionSection(context: Context) {
    var tick by remember { mutableStateOf(0) }
    val canSetDnd by BridgeController.canSetDnd.collectAsState()
    val shizukuReady by BridgeController.shizukuReady.collectAsState()

    // 从系统设置页返回时要重新判断，否则一直显示「去授权」
    val overlayLauncher = rememberLauncherForActivityResult(
        ActivityResultContracts.StartActivityForResult()
    ) { tick++ }

    val hasNotifListener = NotificationListener.hasReadNotificationsPermission(context)
    val hasPostNotif = Build.VERSION.SDK_INT < 33 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) ==
        PackageManager.PERMISSION_GRANTED
    val hasOverlay = remember(tick) {
        runCatching { Settings.canDrawOverlays(context) }.getOrDefault(false)
    }

    SectionCard(title = "权限") {
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
                title = "通知显示",
                subtitle = "显示后台运行状态通知",
                granted = hasPostNotif,
                onClick = {
                    context.startActivity(
                        Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                            .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                    )
                }
            )
        }
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
        // 不是用来画悬浮窗的：Android 15 起「从后台打开应用」只有这一种用户可授权的豁免
        PermissionRow(
            title = "显示在其他应用的上层",
            subtitle = "在电脑上点一条通知时，手机才能直接打开对应应用",
            granted = hasOverlay,
            onClick = {
                runCatching {
                    overlayLauncher.launch(
                        Intent(
                            Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                            Uri.parse("package:${context.packageName}")
                        )
                    )
                }
            }
        )
        PermissionRow(
            title = "忽略电池优化",
            subtitle = "熄屏后保持连接稳定（MIUI 还需把省电策略设为无限制）",
            granted = remember(tick) { isIgnoringBatteryOptimizations(context) },
            onClick = { requestIgnoreBatteryOptimizations(context) }
        )
        // 普通应用开不了 WiFi，只能借 Shizuku 的 shell 身份执行 cmd wifi
        PermissionRow(
            title = "Shizuku 授权",
            subtitle = if (!ShizukuHelper.isBinderAlive())
                "没检测到 Shizuku 服务，请先启动 Shizuku"
            else "让电脑能经蓝牙打开 / 切换手机 WiFi",
            granted = shizukuReady,
            onClick = {
                if (!ShizukuHelper.isBinderAlive()) return@PermissionRow
                BridgeController.requestShizukuPermission()
            }
        )

        // 前台服务的通知 Android 不允许应用自己撤掉，只能让用户在这里关掉
        ActionRow(
            title = "隐藏常驻通知",
            subtitle = "系统不允许应用自己撤掉前台通知，可以点进系统设置把它关掉",
            onClick = {
                runCatching {
                    context.startActivity(
                        Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
                            .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                            .putExtra(Settings.EXTRA_CHANNEL_ID, "bridge_service_v2")
                    )
                }
            }
        )
    }

    // 从系统设置页回来后重新读一次 Shizuku 授权（用户可能刚点过允许）
    androidx.compose.runtime.LaunchedEffect(tick) {
        BridgeController.refreshShizukuState()
    }
}

// ---------------- WiFi 兜底 ----------------

@Composable
private fun WifiFallbackSection() {
    var ssid by remember { mutableStateOf(Prefs.targetSsid) }
    var password by remember { mutableStateOf(Prefs.targetWifiPassword) }
    var enabled by remember { mutableStateOf(Prefs.wifiFallbackEnabled) }

    SectionCard(title = "WiFi 兜底") {
        HintText(
            "电脑接着有线网、手机却忘了开 WiFi 或连错网络时，电脑会通过已配对的蓝牙" +
                "直接连过来，让手机切回下面的目标网络。" +
                "第一次使用前需要在 Windows 上把手机配对一次。"
        )
        VSpace(4)
        TextField(
            value = ssid,
            onValueChange = {
                ssid = it
                Prefs.targetSsid = it
            },
            modifier = Modifier.fillMaxWidth(),
            label = "目标 WiFi 名称",
            singleLine = true
        )
        VSpace(8)
        TextField(
            value = password,
            onValueChange = {
                password = it
                Prefs.targetWifiPassword = it
            },
            modifier = Modifier.fillMaxWidth(),
            label = "密码（可留空）",
            singleLine = true
        )
        HintText(
            "留空也能用：先「关→开」WiFi 让系统自己连已保存的网络。" +
                "只有系统选错了网络时才需要密码，用来精确指定。"
        )
        VSpace(4)
        SwitchRow(
            title = "启用蓝牙兜底",
            subtitle = "电脑经已配对的蓝牙直接调起切换，不需要手机广播",
            checked = enabled,
            onCheckedChange = {
                enabled = it
                Prefs.wifiFallbackEnabled = it
            }
        )
    }
}

// ---------------- 使用说明 ----------------

@Composable
private fun GuideSection() {
    SectionCard(title = "使用说明") {
        HintText(
            "1. 电脑端运行「Message」并保持托盘常驻\n" +
                "2. 手机填好电脑局域网 IP 与端口，打开「推送服务」\n" +
                "3. 首次连接时在电脑弹窗看 PIN，在手机输入完成配对\n" +
                "4. 之后手机的通知会自动推送到电脑\n\n" +
                "连不上时依次检查：电脑端是否在运行、IP 是否变了、" +
                "Windows 防火墙有没有放行 17001 端口。"
        )
    }
}

@Composable
private fun AboutSection() {
    SectionCard(title = "关于") {
        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Text("Message", style = MiuixTheme.textStyles.body1, modifier = Modifier.weight(1f))
            Text(
                "1.0",
                style = MiuixTheme.textStyles.body2,
                color = MiuixTheme.colorScheme.onBackgroundVariant
            )
        }
        HintText("JSON 行协议 + TCP 长连接，参考 KDE Connect 的实现思路。")
    }
}

// ---------------- 电池优化 ----------------

private fun isIgnoringBatteryOptimizations(context: Context): Boolean {
    val pm = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    return pm.isIgnoringBatteryOptimizations(context.packageName)
}

private fun requestIgnoreBatteryOptimizations(context: Context) {
    runCatching {
        context.startActivity(
            Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                .setData(Uri.fromParts("package", context.packageName, null))
        )
    }.onFailure {
        runCatching {
            context.startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
        }.onFailure {
            runCatching {
                context.startActivity(
                    Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS)
                        .setData(Uri.fromParts("package", context.packageName, null))
                )
            }
        }
    }
}
