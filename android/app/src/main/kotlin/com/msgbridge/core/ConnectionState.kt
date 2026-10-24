package com.msgbridge.core

/** 连接状态 */
enum class ConnectionState {
    /** 未连接 / 已断开 */
    Disconnected,

    /** 正在连接电脑 */
    Connecting,

    /** 已连接，等待配对（输入 PIN 或电脑尚未确认） */
    AwaitingPair,

    /** 已连接且已配对，正在推送 */
    Connected,
}
