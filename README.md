# Message（MessageBridge）

将**安卓手机的通知实时推送到 Windows 电脑**的双端项目，实现方式参考本目录下的 KDE Connect（`kdeconnect-android` / `kdeconnect-kde`）。

- 安卓端：Kotlin + Jetpack Compose，界面使用 **Miuix**（小米 HyperOS 风格组件库）
- Windows 端：C# / WPF，托盘常驻 + 系统通知气泡 + 消息列表窗口
- 连接方式：局域网 TCP 长连接，**手动填写电脑 IP + PIN 码配对**（简化版，无需自动发现与证书体系）

## 目录结构

```
msgbridge/
├── README.md                     # 本文档
├── android/                      # 安卓端（Android Studio 工程）
│   ├── gradlew(.bat) / gradle/wrapper   # Gradle 9.6 wrapper
│   └── app/src/main/
│       ├── kotlin/com/msgbridge/
│       │   ├── core/             # 协议、TCP 客户端、偏好存储、连接控制器
│       │   ├── service/          # 前台保活服务、通知监听、开机自启
│       │   └── ui/               # Miuix 风格主界面（状态/配对/设置/权限）
│       └── res/                  # 图标、主题、字符串
└── windows/                      # Windows 端（.NET 8 WPF 工程）
    ├── MessageBridge.sln
    ├── MessageBridge/
    │   ├── App.xaml(.cs)         # 启动、托盘、事件调度
    │   ├── MainWindow.xaml(.cs)  # 消息列表窗口
    │   ├── PairWindow.xaml(.cs)  # 配对弹窗（显示 PIN）
    │   ├── Core/                 # 协议、TCP 服务端、信任存储、消息存储
    │   ├── Services/Tray.cs      # 托盘 + 系统气泡 + 暂停推送/开机自启菜单
    │   ├── Services/IconStore.cs # 图标落盘（JSON 只存文件名）
    │   ├── Services/ToastBridge.cs # 系统通知（合并/静默/方形图标）
    │   └── Resources/msgicon.ico
    ├── NuGet.Config              # 项目级源配置（清除机器上失效的本地源）
    └── smoke_test.py             # 协议冒烟测试脚本（模拟手机端）
```

## 架构与协议

参考 KDE Connect 的 `NetworkPacket` 设计：**每条消息是一行 UTF-8 JSON，以换行符结尾**，经 TCP 传输：

```json
{"type":"ping","id":1760000000000,"body":{}}
{"type":"pair.request","id":1760000000001,"body":{"deviceId":"...","deviceName":"Xiaomi 13","platform":"android","version":1}}
{"type":"notification","id":1760000000002,"body":{"id":"key","appName":"微信","packageName":"com.tencent.mm","title":"...","text":"...","time":1760000000000,"icon":"base64...","isCancel":false,"silent":false}}
```

| 包类型 | 方向 | 说明 |
| --- | --- | --- |
| `pair.request` | 手机 → 电脑 | 请求配对（携带 deviceId / deviceName） |
| `pair.confirm` | 手机 → 电脑 | 提交电脑端显示的 6 位 PIN |
| `pair.ok` / `pair.reject` | 电脑 → 手机 | 配对结果 |
| `notification` | 手机 → 电脑 | 通知（`isCancel=true` 表示通知被移除，电脑端不会把它记为一条消息） |
| `ping` / `pong` | 双向 | 保活 |

### notification 包字段（完整移植 KDE Connect NotificationsPlugin）

| 字段 | 说明 |
| --- | --- |
| `id` | 通知 key（取消/更新时对应同一 key） |
| `appName` / `packageName` | 应用名 / 包名 |
| `title` / `text` | 标题 / 内容（群聊时 title=群名，text=`发送人: 内容`） |
| `ticker` | 通知栏文本 |
| `time` | 通知发布时间戳（ms） |
| `icon` | 应用图标 PNG base64（大图标 → 小图标 → 应用图标三级提取） |
| `isCancel` | 是否为移除事件 |
| `isClearable` | 是否可滑动清除 |
| `silent` | 是否静默（存量同步 / 更新时仅提醒一次） |
| `conversation` | 会话历史 `[{sender, content}]`（微信/QQ 等 MessagingStyle 通知） |
| `groupName` | 群聊名称 |
| `actions` | 动作按钮标题数组（排除带输入框的回复动作） |
| `requestReplyId` | 存在可回复动作时的标识 |

**手机端不可见的通知不会推送**（与 KDE Connect 一致）：前台服务通知、常驻通知、仅本机通知、通知组摘要、媒体播放控制、系统低电量、自己的通知、被暂停的应用（专注模式/数字健康）。通知 posted 后延迟 50ms 再发，防止「刚弹出就被移除」的通知也推送到电脑端。

### 配对流程（手动 IP + PIN）

1. 电脑端运行「Message」，监听 `17001` 端口（可在 `%LocalAppData%\MessageBridge\config.json` 修改）。**首次运行若系统弹出防火墙询问，请允许**；连接不上时先检查防火墙是否放行 17001/TCP。
2. 手机填入电脑局域网 IP 与端口，开启推送服务。
3. 手机连接后发送 `pair.request`；若电脑端已信任该设备则直接放行，否则电脑弹窗显示 6 位 PIN（10 分钟有效）。
4. 手机输入 PIN 发送 `pair.confirm`，校验通过即完成配对；电脑端将设备写入 `trust.json`，后续重连自动放行。

## 与 KDE Connect 的实现对照

| 能力 | KDE Connect | 本项目（简化） |
| --- | --- | --- |
| 消息格式 | `NetworkPacket`（JSON 行） | 同款 JSON 行协议 |
| 通知推送 | `NotificationListenerService` + 过滤（前台服务/常驻/媒体等） | 同款过滤逻辑 + 图标三级提取 + 会话/动作字段 |
| 设备发现 | UDP 广播 + 证书 | 手动 IP |
| 配对加密 | TLS + 双向证书 | PIN 一次性验证（局域网自用） |
| 桌面端 | KDE Plasma 插件体系 | WPF 托盘应用 |

## 构建

### Windows 端

需要 [.NET SDK 8+](https://dotnet.microsoft.com/download)。

```powershell
cd windows
dotnet build MessageBridge.sln -c Debug
# 产物：windows\MessageBridge\bin\Debug\net8.0-windows\MessageBridge.exe
```

> `NuGet.Config` 已配置为仅使用 nuget.org，并清除机器上可能失效的本地源/回退文件夹。

### 安卓端

需要 JDK 17+ 与 Android SDK（`ANDROID_HOME` 指向 SDK，含 platform 37；compileSdk 37）。

```powershell
cd android
.\gradlew.bat :app:assembleDebug
# 产物：android\app\build\outputs\apk\debug\app-debug.apk
```

> Gradle 发行版下载地址已切换到腾讯云镜像（`mirrors.cloud.tencent.com/gradle`），
> 官方源 `services.gradle.org` 在国内直连经常超时。首次构建会自动下载 Gradle 9.6 与依赖，耗时较长属正常。

## 使用步骤

**电脑端**
1. 运行 `MessageBridge.exe`，程序常驻托盘并开始监听 17001。
2. 首次运行若系统弹出防火墙询问，请选择「允许」。
3. 收到手机配对请求时弹出窗口，把 PIN 告诉手机端。
4. 托盘右键菜单：**暂停推送 30 分钟**（暂停期间消息仍会记录，只是不弹通知）、**开机自启**（写 HKCU Run，不需要管理员）。
5. 连续多条通知会合并显示成「N 条新消息」+ 最近 3 条摘要；手机端标记为静默的通知在电脑端也不发声。

**手机端**
1. 安装 APK，打开「Message」。
2. 填写电脑 IP 与端口（默认 17001），开启「推送服务」。
3. 按提示授予权限：**通知使用权**（推送通知必需）、**通知显示**（Android 13+ 显示常驻通知）、**忽略电池优化**。
4. 连接后输入电脑端显示的 PIN 完成配对。
5. 之后手机的通知会自动推送到电脑（托盘气泡 + 列表窗口）。
6. 不需要界面时点「隐藏到后台（从最近任务移除）」：界面与最近任务项都会消失，推送服务继续常驻。
7. 「免打扰时暂停推送」默认开启：手机进入勿扰模式后不再向电脑推送通知（免打扰状态由 `interruptionFilter` 判断）。
8. 下拉控制中心里有一个「Message」快捷开关，可以一键开关推送服务，和 App 内「推送服务」开关状态始终一致。

### 小米/MIUI/HyperOS 保活提示

前台服务 + WakeLock 已实现，但 MIUI 的「省电策略」独立于 Android 白名单：请到 **设置 → 应用 → 应用管理 → Message → 省电策略 → 无限制**，并将「Message」加入后台运行白名单，否则熄屏后连接可能被系统切断。

## 验证

- Windows 端：`dotnet build` 0 警告 0 错误；`smoke_test.py` 已跑通 `ping→pong`、信任设备放行、通知收包与持久化全链路（`isCancel` 通知不落盘）。
- 安卓端：`gradlew :app:assembleDebug` 构建产物 APK。

## 数据存放（Windows）

- `%LocalAppData%\MessageBridge\config.json`：端口配置（默认 17001）
- `%LocalAppData%\MessageBridge\trust.json`：已配对设备
- `%LocalAppData%\MessageBridge\messages.json`：消息记录（只保留最近 50 条，超出时自动丢弃最旧的；图标只存文件名）
- `%LocalAppData%\MessageBridge\icons\`：通知图标 png（按内容哈希命名，不再被消息引用时自动清理）
- `%LocalAppData%\MessageBridge\logs\toast.log`：Toast 发送失败时的排查日志
