package com.msgbridge.core

import org.json.JSONObject

/**
 * 消息桥协议。
 *
 * 参考 KDE Connect 的 NetworkPacket：每条消息为一行 UTF-8 JSON，
 * 以换行符结尾，结构为 {"protoVer":1,"type":"...","id":<毫秒时间戳>,"body":{...}}。
 *
 * 包类型：
 *  - pair.request                    手机 -> 电脑：请求配对（body: deviceId / deviceName / platform）
 *  - pair.confirm                    手机 -> 电脑：提交电脑端显示的 PIN（body: pin）
 *  - pair.ok                         电脑 -> 手机：配对成功
 *  - pair.reject                     电脑 -> 手机：配对失败 / PIN 错误
 *  - notification                    手机 -> 电脑：通知
 *                                    （body: id/appName/packageName/title/text/ticker/time/icon/
 *                                     isCancel/silent/isOngoing/isSync）
 *  - sms                             手机 -> 电脑：短信（body: id/address/date/body/read）
 *  - command.clearNotifications      电脑 -> 手机：清空可清除的通知（常驻通知清不掉）
 *  - command.setInterruptionFilter   电脑 -> 手机：设置勿扰（filter: 0=开启勿扰, 2=关闭勿扰）
 *  - command.setWindowsToast         双向：修改 Windows 气泡开关（enable: bool）
 *  - command.openNotification        电脑 -> 手机：用户点了电脑上的某条通知，
 *                                    让手机打开它（body: id=通知 key / packageName）
 *  - state.interruptionFilter        手机 -> 电脑：手机勿扰状态（filter / canSet）
 *  - state.windowsToast              电脑 -> 手机：Windows 气泡开关状态（enable）
 *  - ping / pong                     双向保活
 */
object BridgeProtocol {
    const val TYPE_PAIR_REQUEST = "pair.request"
    const val TYPE_PAIR_CONFIRM = "pair.confirm"
    const val TYPE_PAIR_OK = "pair.ok"
    const val TYPE_PAIR_REJECT = "pair.reject"
    const val TYPE_NOTIFICATION = "notification"
    const val TYPE_SMS = "sms"

    /** 电脑 -> 手机：清空手机通知栏 */
    const val TYPE_COMMAND_CLEAR_NOTIFICATIONS = "command.clearNotifications"

    /** 电脑 -> 手机：设置勿扰 */
    const val TYPE_COMMAND_SET_INTERRUPTION_FILTER = "command.setInterruptionFilter"

    /** 双向：修改 Windows 气泡开关 */
    const val TYPE_COMMAND_SET_WINDOWS_TOAST = "command.setWindowsToast"

    /** 电脑 -> 手机：打开某条通知对应的界面（id 为空或通知已消失时退化成打开应用） */
    const val TYPE_COMMAND_OPEN_NOTIFICATION = "command.openNotification"

    /** 手机 -> 电脑：上报勿扰状态 */
    const val TYPE_STATE_INTERRUPTION_FILTER = "state.interruptionFilter"

    /** 电脑 -> 手机：上报 Windows 气泡开关状态 */
    const val TYPE_STATE_WINDOWS_TOAST = "state.windowsToast"

    const val TYPE_PING = "ping"
    const val TYPE_PONG = "pong"
    const val TYPE_BYE = "bye"

    const val DEFAULT_PORT = 17001
    const val PROTOCOL_VERSION = 1

    /** 勿扰：需要修改时传这个值开启勿扰 */
    const val FILTER_DND = 0

    /** 勿扰：传这个值关闭勿扰 */
    const val FILTER_ALL = 2

    fun serialize(type: String, body: JSONObject = JSONObject()): String {
        val root = JSONObject()
        root.put("protoVer", PROTOCOL_VERSION)
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
