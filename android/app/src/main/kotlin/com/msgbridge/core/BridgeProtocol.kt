package com.msgbridge.core

import org.json.JSONObject

/**
 * 消息桥协议。
 *
 * 参考 KDE Connect 的 NetworkPacket：每条消息为一行 UTF-8 JSON，
 * 以换行符结尾，结构为 {"type":"...","id":<毫秒时间戳>,"body":{...}}。
 *
 * 包类型：
 *  - pair.request   手机 -> 电脑：请求配对（body: deviceId / deviceName / platform）
 *  - pair.confirm   手机 -> 电脑：提交电脑端显示的 PIN（body: pin）
 *  - pair.ok        电脑 -> 手机：配对成功
 *  - pair.reject    电脑 -> 手机：配对失败 / PIN 错误
 *  - notification   手机 -> 电脑：通知（body: id/appName/packageName/title/text/ticker/time/icon/isCancel/silent）
 *  - sms            手机 -> 电脑：短信（body: id/address/date/body/read）
 *  - ping / pong    双向保活
 */
object BridgeProtocol {
    const val TYPE_PAIR_REQUEST = "pair.request"
    const val TYPE_PAIR_CONFIRM = "pair.confirm"
    const val TYPE_PAIR_OK = "pair.ok"
    const val TYPE_PAIR_REJECT = "pair.reject"
    const val TYPE_NOTIFICATION = "notification"
    const val TYPE_SMS = "sms"
    const val TYPE_PING = "ping"
    const val TYPE_PONG = "pong"
    const val TYPE_BYE = "bye"

    const val DEFAULT_PORT = 17001
    const val PROTOCOL_VERSION = 1

    fun serialize(type: String, body: JSONObject = JSONObject()): String {
        val root = JSONObject()
        root.put("type", type)
        root.put("id", System.currentTimeMillis())
        root.put("body", body)
        return root.toString() + "\n"
    }

    fun parse(line: String): JSONObject? {
        return try {
            JSONObject(line)
        } catch (e: Exception) {
            null
        }
    }
}
