package com.msgbridge.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import top.yukonga.miuix.kmp.theme.ColorSchemeMode
import top.yukonga.miuix.kmp.theme.MiuixTheme
import top.yukonga.miuix.kmp.theme.ThemeController

/**
 * 应用主题：跟随系统深/浅色，使用 Miuix（HyperOS 风格）。
 */
@Composable
fun MessageBridgeTheme(content: @Composable () -> Unit) {
    val controller = remember { ThemeController(ColorSchemeMode.System) }
    MiuixTheme(controller = controller, content = content)
}
