# Message（MessageBridge）

将**安卓手机的通知实时推送到 Windows 电脑**的双端项目，实现方式参考本目录下的 KDE Connect（`kdeconnect-android` / `kdeconnect-kde`）。

- 安卓端：Kotlin + Jetpack Compose，界面使用 **Miuix**（小米 HyperOS 风格组件库）
- Windows 端：C# / **WinUI 3**，Fluent 风格界面（Mica 背景 + 自定义标题栏），托盘常驻（静默启动）+ 系统通知气泡 + 单窗口双页（实时通知 / 历史记录）
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
└── windows/                      # Windows 端（.NET 8 WinUI 3 工程）
    ├── MessageBridge.sln
    ├── MessageBridge/
    │   ├── App.xaml(.cs)         # 启动、托盘、事件调度、两个开关的真相源
    │   ├── MainWindow.xaml(.cs)  # 历史消息窗口（Mica + 自定义标题栏）
    │   ├── PairWindow.xaml(.cs)  # 配对弹窗（显示 PIN）
    │   ├── Core/                 # 协议、TCP 服务端、信任存储、消息存储、快照存储
    │   ├── Services/Tray.cs      # 托盘图标（WinForms NotifyIcon，跟随系统主题换线条色）
    │   ├── Services/WindowScale.cs # 窗口 DPI 换算（逻辑像素 ↔ 物理像素）
    │   ├── Services/IconStore.cs # 图标落盘（JSON 只存文件名）
    │   ├── Services/ToastBridge.cs # 系统通知（合并/静默/方形图标）
    │   ├── NoVsAppx.targets      # 没有 Visual Studio 时绕开 MSIX 打包任务的补丁
    │   └── Resources/            # msgicon.ico（应用图标）+ tray_dark/light.ico（托盘线条图标）
    ├── tools/make_tray_icons.py  # 生成纯色线条托盘图标（Pillow）
    ├── NuGet.Config              # 项目级源配置（清除机器上失效的本地源）
    └── smoke_test.py             # 协议冒烟测试脚本（模拟手机端）
```

## 架构与协议

参考 KDE Connect 的 `NetworkPacket` 设计：**每条消息是一行 UTF-8 JSON，以换行符结尾**，经 TCP 传输。
所有包都带 `protoVer` 字段：

```json
{"protoVer":1,"type":"ping","id":1760000000000,"body":{}}
{"protoVer":1,"type":"pair.request","id":1760000000001,"body":{"deviceId":"...","deviceName":"Xiaomi 13","platform":"android","version":1}}
{"protoVer":1,"type":"notification","id":1760000000002,"body":{"id":"key","appName":"微信","packageName":"com.tencent.mm","title":"...","text":"...","time":1760000000000,"icon":"base64...","isCancel":false,"silent":false,"isOngoing":false,"isSync":false}}
```

| 包类型 | 方向 | 说明 |
| --- | --- | --- |
| `pair.request` | 手机 → 电脑 | 请求配对（携带 deviceId / deviceName） |
| `pair.confirm` | 手机 → 电脑 | 提交电脑端显示的 6 位 PIN |
| `pair.ok` / `pair.reject` | 电脑 → 手机 | 配对结果 |
| `notification` | 手机 → 电脑 | 通知（`isCancel=true` 表示通知被移除） |
| `command.clearNotifications` | 电脑 → 手机 | 清空手机通知栏（`cancelAllNotifications`，常驻通知系统本来也不允许清） |
| `command.setInterruptionFilter` | 电脑 → 手机 | 设置手机勿扰（`filter`: 0=开启勿扰, 2=关闭勿扰） |
| `command.setWindowsToast` | 双向 | 修改 Windows 气泡开关（`enable`: bool） |
| `command.openNotification` | 电脑 → 手机 | 打开某条通知对应的界面（`id` = 手机端通知 key，`packageName` 兜底；见「点击通知，在手机上打开」） |
| `state.interruptionFilter` | 手机 → 电脑 | 上报手机勿扰状态（`filter` 同上，`canSet` 表示是否有权限修改） |
| `state.windowsToast` | 电脑 → 手机 | 上报 Windows 气泡开关状态（`enable`） |
| `ping` / `pong` | 双向 | 保活 |

```json
{"protoVer":1,"type":"command.clearNotifications","id":1760000000003,"body":{}}
{"protoVer":1,"type":"command.setInterruptionFilter","id":1760000000004,"body":{"filter":2}}
{"protoVer":1,"type":"command.setWindowsToast","id":1760000000005,"body":{"enable":true}}
{"protoVer":1,"type":"command.openNotification","id":1760000000008,"body":{"id":"0|com.tencent.mm|123|null|10286","packageName":"com.tencent.mm"}}
{"protoVer":1,"type":"state.interruptionFilter","id":1760000000006,"body":{"filter":2,"canSet":true}}
{"protoVer":1,"type":"state.windowsToast","id":1760000000007,"body":{"enable":true}}
```

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
| `isOngoing` | 是否常驻通知（前台服务 / Ongoing）。当前手机端已整体过滤常驻通知，该字段恒为 `false`，保留以便以后需要「常驻只进快照」时启用 |
| `isSync` | 是否为重连后的批量补发，电脑端不弹气泡、不写历史 |
| `conversation` | 会话历史 `[{sender, content}]`（微信/QQ 等 MessagingStyle 通知） |
| `groupName` | 群聊名称 |
| `actions` | 动作按钮标题数组（排除带输入框的回复动作） |
| `requestReplyId` | 存在可回复动作时的标识 |

**手机端不可见的通知不会推送**（与 KDE Connect 一致）：前台服务通知、常驻通知、仅本机通知、通知组摘要、媒体播放控制、系统低电量、自己的通知、被暂停的应用（专注模式/数字健康）。

> 常驻通知（`FLAG_FOREGROUND_SERVICE` / `FLAG_ONGOING_EVENT`）为什么也不推：手机通知栏里它们大多被系统折叠进「静默通知 / 更多通知」，用户根本看不到，推到电脑实时通知页就是一堆对不上号的噪音（小米智能卡、跨屏协同、语音唤醒……）。试过用 `getRanking().importance` 区分「是否真的可见」，但这些常驻通知普遍是 DEFAULT/LOW 级别，靠重要度分不出来，所以整体过滤。

通知 posted 后延迟 50ms 再发，防止「刚弹出就被移除」的通知也推送到电脑端。

### 配对流程（手动 IP + PIN）

1. 电脑端运行「Message」，监听 `17001` 端口（可在 `%LocalAppData%\MessageBridge\config.json` 修改）。**首次运行若系统弹出防火墙询问，请允许**；连接不上时先检查防火墙是否放行 17001/TCP。
2. 手机填入电脑局域网 IP 与端口，开启推送服务。
3. 手机连接后发送 `pair.request`；若电脑端已信任该设备则直接放行，否则电脑弹窗显示 6 位 PIN（10 分钟有效）。
4. 手机输入 PIN 发送 `pair.confirm`，校验通过即完成配对；电脑端将设备写入 `trust.json`，后续重连自动放行。

## 与 KDE Connect 的实现对照

| 能力 | KDE Connect | 本项目（简化） |
| --- | --- | --- |
| 消息格式 | `NetworkPacket`（JSON 行） | 同款 JSON 行协议（带 `protoVer`） |
| 通知推送 | `NotificationListenerService` + 过滤（前台服务/常驻/媒体等） | 同款过滤逻辑 + 图标三级提取 + 会话/动作字段 |
| 勿扰联动 | 暂停推送 | 不拦截推送；勿扰开关两端互相控制，状态双向同步 |
| 设备发现 | UDP 广播 + 证书 | 手动 IP |
| 配对加密 | TLS + 双向证书 | PIN 一次性验证（局域网自用） |
| 桌面端 | KDE Plasma 插件体系 | WinUI 3 托盘应用 + 实时通知页 |

## 两个独立开关

| 开关 | 控制什么 | 状态存在哪 | 谁能改 |
| --- | --- | --- | --- |
| **Windows 气泡** | 电脑是否弹系统 Toast（关掉只记录、不打扰） | 电脑 `config.json` 的 `enableWindowsToast` | 电脑面板 / 手机 App，改完立刻同步另一端 |
| **手机勿扰** | 手机本机系统勿扰（是否响铃） | 手机系统 | 电脑实时通知页 / 手机 App / 系统下拉面板，状态实时上报电脑 |

两者互不影响，**都和推送无关**：通知始终同步到电脑并写入历史，是否弹气泡只看「Windows 气泡」开关。
手机没有授予「勿扰访问权限」时，电脑面板上的勿扰开关会置灰。

## 实时通知（主窗口第一页）

主窗口是一个窗口两页，用顶部的 `实时通知 / 历史记录` 切换喵。托盘图标**左键**打开时直接落在「实时通知」，右键「打开 Message」落在「历史记录」。

`实时通知` 和 `messages.json` 历史是两套独立数据：

- **只显示手机此刻还挂着的通知**：新增 → 插入，`isCancel` → 移除，同 key → 原地更新
- 顶部一行：`电脑气泡` 开关、`手机勿扰` 开关、`清空手机通知` 按钮
- **断连即清空**，重连后手机会把所有现存通知批量补发一次重建快照
- 每条通知两行：`来源 · 标题` + 内容，一屏能看到更多条
- 图标 48 DIP，和 Windows 通知卡片里的 `appLogoOverride` 尺寸对齐
- `清空手机通知` 调用手机 `cancelAllNotifications()`：普通通知被清掉，快照随之清空
- 只包含手机通知栏里用户真正看得见的通知（常驻通知已在手机端过滤掉）
- **图标/名称兜底**：连接后批量补发时，手机端对部分「挂了一段时间」的通知拿不到图标、应用名也会退化成包名。电脑端会用之前正常推送时记下的「包名 → 应用名 + 图标」补上（存在 `messages.json` 里，重启后依然有效）。

## 点击通知，在手机上打开

两页都可以点，点完之后手机上的效果和在手机上点那条通知一致：**实时通知**里点微信群消息会直接进那个群聊；**历史记录**里的通知在手机上早就消失了，只能打开对应应用。窗口底部会弹一条 `已让手机打开 XX` 的提示。

协议上很简单，电脑发一条 `command.openNotification`（`id` = 手机端通知 key，`packageName` 兜底）：

- **通知还挂在通知栏上**：手机端 `send()` 它自带的 `contentIntent`，效果等于在手机上点这条通知 —— 微信会直接跳进对应聊天窗口，不是停在首页。
- **通知已经没了**（历史记录里的旧消息、或者被别的通知顶掉）：按 `packageName` 打开应用。

两条路都必须先挂那个 1×1 的透明叠加窗口（见下文），`PendingIntent.send()` 也不例外：Android 15 的判定看的是「发送方当前有没有叠加窗口」（`realCallingUidHasNonAppVisibleWindow`），没有就是 `BAL_BLOCK`，有就放行。

### 需要「显示在其他应用的上层」权限

Android 15 起，应用从后台启动界面会被系统一律拦下（`BAL_BLOCK`），**连异常都不给**，表现就是「点了没反应」。用户能给的唯一豁免就是这个权限（系统日志里叫 `BAL_ALLOW_SAW_PERMISSION`）：

- 手机 App → 权限卡片 → **显示在其他应用的上层** → 去授权
- 路径：设置 → 应用设置 → 应用管理 → Message → 其他权限 → 显示在其他应用的上层

它不是用来画悬浮窗的。系统判定时还要求「当前真的有叠加窗口」（`hasNonAppVisibleWindow`），所以真正启动前会临时挂一个 `1x1` 的全透明 `TYPE_APPLICATION_OVERLAY` 窗口，等 `system_server` 把这个窗口记到本应用名下（约 120 ms）再 `startActivity`，启动后 500 ms 摘掉。没有这个权限就只能硬试，多半被系统拦掉。

> 顺带踩过的坑：`getLaunchIntentForPackage()` 在 Android 11+ 会因为「包可见性」返回 `null`，需要在 Manifest 里声明 `<queries>`（本项目声明的是「带桌面入口的应用」），否则同样表现为点了没反应。

## 与 KDE Connect 的实现对照

## 构建

### Windows 端

需要 [.NET SDK 8+](https://dotnet.microsoft.com/download) 和 **Windows App Runtime 1.7**（微软商店装「Windows App Runtime 1.7」或从 [aka.ms/windowsappsdk](https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe) 安装）。

```powershell
cd windows
dotnet build MessageBridge.sln -c Debug
# 产物：windows\MessageBridge\bin\Debug\net8.0-windows10.0.19041.0\win-x64\MessageBridge.exe
```

> `NuGet.Config` 已配置为仅使用 nuget.org，并清除机器上可能失效的本地源/回退文件夹。

关于 WinUI 3 的两个构建注意点：

- **不需要 Visual Studio**。机器的 `dotnet` SDK 里没有 MSIX 打包用的 MSBuild 任务（`Microsoft.Build.AppxPackage.dll` 等），Windows App SDK 的 `MrtCore.PriGen.targets` 会因此直接报 `MSB4062`。项目用 `NoVsAppx.targets` 把纯打包相关的 Target 覆盖成空实现绕过去，再由构建后步骤用 `makepri.exe`（来自 `Microsoft.Windows.SDK.BuildTools` NuGet 包）生成应用自己的 `resources.pri`——缺了它 WinUI 启动会 `0xC000027B` 崩在 `Microsoft.UI.Xaml.dll`。
- **框架依赖模式**（`WindowsAppSDKSelfContained=false`）。自包含模式会把 300 多个运行时 DLL 拷到输出目录，但在这台机器上会和系统里已装的 Windows App Runtime 打架，同样 `0xC000027B` 启动崩溃。所以目标机器必须装 Windows App Runtime 1.7。

换托盘图标的线条样式：

```powershell
cd windows\tools
python make_tray_icons.py   # 重新生成 Resources/tray_dark.ico 与 tray_light.ico
```

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
1. 运行 `MessageBridge.exe`，程序**静默启动**（只有托盘图标，不弹窗口），开始监听 17001。
2. 首次运行若系统弹出防火墙询问，请选择「允许」。
3. 收到手机配对请求时弹出窗口，把 PIN 告诉手机端。
4. 托盘**左键**：打开主窗口的「实时通知」页（当前手机还挂着的通知 + 两个开关 + 清空按钮）。
5. 托盘**双击** / 右键「打开 Message」：打开主窗口的「历史记录」页（读 `messages.json`）。
6. 两页之间用窗口顶部的 `实时通知 / 历史记录` 切换，同一个窗口，不用来回开。
7. 托盘右键菜单：**暂停推送 30 分钟**（暂停期间消息仍会记录，只是不弹通知）、**开机自启**（写 HKCU Run，不需要管理员）、**退出**。
8. 连续多条通知会合并显示成「N 条新消息」+ 最近 3 条摘要；手机端标记为静默的通知在电脑端也不发声。

**手机端**
1. 安装 APK，打开「Message」。
2. 填写电脑 IP 与端口（默认 17001），开启「推送服务」。
3. 按提示授予权限：**通知使用权**（推送通知必需）、**通知显示**（Android 13+ 显示常驻通知）、**勿扰访问权限**（电脑远程开关勿扰必需）、**显示在其他应用的上层**（在电脑上点通知时直接打开应用必需，见上文）、**忽略电池优化**。
4. 连接后输入电脑端显示的 PIN 完成配对。
5. 之后手机的通知会自动推送到电脑（是否弹气泡看电脑侧的开关）。
6. 「系统开关」里可以直接改「电脑系统通知（气泡）」和「手机勿扰」，状态跟电脑实时同步。
7. 不需要界面时点「隐藏到后台（从最近任务移除）」：界面与最近任务项都会消失，推送服务继续常驻。
8. 下拉控制中心里有一个「Message」快捷开关，可以一键开关推送服务，和 App 内「推送服务」开关状态始终一致。

### 小米/MIUI/HyperOS 保活提示

前台服务 + WakeLock 已实现，但 MIUI 的「省电策略」独立于 Android 白名单：请到 **设置 → 应用 → 应用管理 → Message → 省电策略 → 无限制**，并将「Message」加入后台运行白名单，否则熄屏后连接可能被系统切断。

## 验证

- Windows 端：`dotnet build` 0 警告 0 错误；`smoke_test.py` 已跑通 `ping→pong`、信任设备放行、通知收包与持久化全链路（`isCancel` 通知不落盘）。
- 安卓端：`gradlew :app:assembleDebug` 构建产物 APK。
- 实时通知页：实测实时新增/更新、`isCancel` 移除、断连清空、重连批量补发；`清空手机通知` 后列表清空。
- 点击打开：HyperOS 3 / Android 16 实测 —— 点电脑上的微信群消息通知，手机微信直接打开对应群聊（不是首页）；通知消失后退化为按包名打开应用。未授予「显示在其他应用的上层」时系统日志为 `BAL_BLOCK`（点了没反应），授予后变为 `BAL_ALLOW_SAW_PERMISSION` 并正常启动。
- 两个开关：电脑端拨动 → `config.json` 落盘 + 手机收到 `state.windowsToast`；手机拨动 → 电脑 `config.json` 同步更新；电脑端拨勿扰 → 手机 `zen_mode` 在 `ZEN_MODE_OFF` ↔ `ZEN_MODE_IMPORTANT_INTERRUPTIONS` 之间切换，并回传 `state.interruptionFilter`。
- 气泡开关关闭时，通知仍写入 `messages.json` 历史，只是不弹 Toast。
- WinUI 3 界面：无 Visual Studio 的 `dotnet build` 构建通过（`resources.pri` 由构建后步骤生成）；主窗口 Mica 背景 + 自定义标题栏 + `SelectorBar` 双页切换均已实测；`AppWindow.Closing` 拦下关闭请求后窗口隐藏、进程继续常驻，再次启动 exe 会通过门铃把窗口唤出。
- 窗口尺寸：150% 缩放的屏幕上按 `GetDpiForWindow` 换算物理像素，主窗口 940×700 DIP 与设计稿一致（`AppWindow.MoveAndResize` 在这台机器上会多除一次缩放系数，故改用 `SetWindowPos`）。
- 勿扰：从 `INTERRUPTION_FILTER_NONE` 改成 `PRIORITY`，只压通知、不再把手机铃声一起静音；设置后读回系统值确认是否真的生效。
- 托盘图标：纯色线条样式，`SystemUsesLightTheme=0`（深色任务栏）时用 `tray_light.ico`，浅色任务栏时用 `tray_dark.ico`，切换系统主题会实时换图。

## 数据存放（Windows）

- `%LocalAppData%\MessageBridge\config.json`：端口与气泡开关（`port` / `enableWindowsToast`）
- `%LocalAppData%\MessageBridge\trust.json`：已配对设备
- `%LocalAppData%\MessageBridge\messages.json`：历史消息记录（只保留最近 50 条，超出时自动丢弃最旧的；图标只存文件名）
- `%LocalAppData%\MessageBridge\icons\`：通知图标 png（按内容哈希命名，不再被历史或快照引用时自动清理）
- `%LocalAppData%\MessageBridge\logs\toast.log`：Toast 发送失败时的排查日志

> 实时通知页的数据**只在内存里**（`SnapshotStore`），不落盘；断连或退出即销毁，重连时由手机重新补发。
