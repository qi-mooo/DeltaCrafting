# T-Display-S3 接入指南

DeltaCrafter 内置 HTTP API,供同一局域网的 LILYGO T-Display-S3 显示四个制造设施、
当前物品、倒计时及运行状态。API 随桌面应用运行,无需另起服务。USB 用于供电/烧录;
设备通过 2.4 GHz Wi-Fi 与电脑通信。

## 桌面端配对

1. 打开 **设置 → T-Display-S3**,开启「局域网设备 API」。默认端口为 `17890`。
2. 点击配对密钥的复制按钮,用于下方 USB 配置。首次启用自动生成 64 位随机密钥。
3. 用 `ipconfig` 查看电脑的局域网 IPv4 地址,例如 `192.168.1.100`。
4. 在 Windows 防火墙中允许专用网络的对应入站端口。管理员 PowerShell 示例:

   ```powershell
   New-NetFirewallRule -DisplayName 'DeltaCrafter S3' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 17890 -Profile Private -RemoteAddress LocalSubnet
   ```

5. 默认只读监控。如需实体按键控制,再开启「允许设备控制」。设置立即生效。

设置页显示监听状态和启动失败原因。端口冲突时换用其他端口,并同步修改固件和防火墙。
API 监听所有网络接口;它使用明文 HTTP,仅在可信局域网使用,不要做公网端口转发。
应用不会自动添加防火墙规则。关闭应用会关闭接口;最小化到托盘不会关闭接口。

配对密钥保存在 `%LocalAppData%\DeltaCrafter\settings.json` 的 `deviceApi` 中,
只通过 `Authorization` 请求头传递,不在 API 响应或正常日志中输出。
重新生成密钥会重新启动监听,旧密钥立即失效,需通过 USB 更新设备配置,无需重新烧录。

## 固件配置与烧录

配套工程位于本仓库 [`firmware/t-display-s3/`](../firmware/t-display-s3/README.md)。
CI 在同一次工作流中分别构建 Windows 客户端和 ESP32-S3 固件。下载
`DeltaCrafter-esp32s3-<提交哈希>` artifact,再解压里面的 `DeltaCrafter-esp32s3.zip`。

在解压目录执行:

```bash
python -m pip install esptool==4.5.1 pyserial==3.5
python flash.py --port COM5
python configure.py --port COM5 --wifi-ssid Your-WiFi --base-url http://192.168.1.100:17890
```

Windows 串口填写实际的 `COM5` 等端口,macOS 填 `/dev/cu.usbmodemXXXX`。
配置工具会隐藏输入 Wi-Fi 密码和配对密钥,保存到板上 NVS 后自动重启。
固件与公开构建产物不包含凭据。以后更改凭据只需重新运行 `configure.py`。
地址只包含电脑局域网地址和端口,不要填 `localhost` 或 `/api/v1/status`。

烧录前确认选择的是目标 S3;手动按住 BOOT,按一下 RESET,然后松开 BOOT。
烧录完成后按一下 RESET 运行;工具不自动切换模式。工具验证校验和并按清单写入镜像,保留其他扇区;
`firmware.bin` 只是应用镜像,不能单独写到地址 0。

从源码构建时,通过 `TDISPLAY_S3_DIR` 指向固定版本的 T-Display-S3 板级仓库,
完整命令见固件 README。默认使用新版 LCD 初始化;早期面板可用
`-DDELTA_LCD_NEW_PANEL=0` 重编译。发布构建拒绝包含本地凭据配置文件。

## 屏幕与按键

| 操作 | 行为 |
| --- | --- |
| GPIO 0 单击 | 循环选择下一项 |
| GPIO 14 单击 | 进入菜单或确认选中设置 |

按住只触发一次,无长按功能。

主页为四分格:左上技术中心、右上工作台、左下制药台、右下防具台,底部状态栏显示游戏状态。
所有页面黑底白字,选中项使用粗双框和角标。Axeuh_UI 提供焦点缓动,页面切换使用滑动过渡。主页焦点循环经过四格和状态栏。
选择设施后按确认进入启用/停用、制造模式设置;计划物品保留在 App 本体修改。
选择状态栏后按确认进入自动循环、Steam 游戏检测、收取后行为设置。每个菜单均提供返回项。
菜单可离线浏览;保存仅在数据新鲜、网络在线且桌面允许控制时生效,成功后刷新状态。
请求超时时显示结果未知,不自动重试。

默认每 3 秒获取一次状态,屏幕目标 60 FPS 绘制,倒计时按设备本地单调时钟推算,无需 NTP。
API 返回的完成时刻来自游戏 OCR。即使倒计时归零,仍保留服务器观测的阶段,
不会凭计时将「制造中」改成「可领取」。禁用计划的设施仍显示已观测的任务。
网络错误时保留四格旧数据,状态栏显示连接中断、游戏状态未知,停止倒计时且禁用保存。
网络任务独立运行,重连不阻塞按键。

每格底部显示制造进度条。客户端确认开工后保存起点,按实际 OCR 完成时刻计算总时长。
首次接管已有制造时总时长未知,显示活动条;离线冻结,确认可领取后填满。
状态栏右侧显示四格电池图标,不显示百分比。GPIO 4 电压估算电量,无效读数显示叉号;
USB 充电电压可能偏高,无电池情况不一定能可靠识别。

## API v1

所有端点均需:

```http
Authorization: Bearer <配对密钥>
```

### GET /api/v1/status

成功返回 `200`,UTF-8 JSON,`Cache-Control: no-store`。字段固定使用 camelCase:

| 字段 | 含义 |
| --- | --- |
| `apiVersion`, `appVersion`, `serverTime` | 协议版本 `1`、应用版本、ISO 8601 服务器时间 |
| `mode`, `detail`, `isRunning` | Idle/WaitingSchedule/Running/Faulted、状态说明、执行锁状态 |
| `autoLoopEnabled`, `controlEnabled` | 自动循环与远程控制开关 |
| `nextRunAt`, `nextRunInSeconds` | 下次调度时间/剩余秒数;关闭自动循环或无计划时为 null |
| `lastRunAt`, `lastRunSummary`, `lastRunFailed` | 上次执行结果;未执行时前两项可为 null |
| `facilities` | 四个设施的数组,见下表 |
| `game.state`, `game.detail`, `game.checkedAt` | Steam 游戏状态 Playing/NotPlaying/Unavailable/Unknown、说明和观测时间;与自动化任务状态独立 |
| `steamDetectionEnabled`, `afterRun`, `settingsSupported` | 游戏检测开关、收取后行为、是否支持设备保存设置 |

| 设施字段 | 含义 |
| --- | --- |
| `key` | workbench / pharmacy-lab / armor-station / tech-center |
| `name`, `enabled` | 中文名称、计划是否启用 |
| `craftMode`, `plannedItemName` | Custom/HourlyProfit/TotalProfit、计划物品 |
| `phase` | Unknown/Idle/Crafting/ReadyToCollect/NeedsManual |
| `itemName` | 游戏中最近观测的当前物品,与计划物品分开 |
| `readyAt`, `remainingSeconds` | OCR 完成时间和非负剩余秒数;无有效制造倒计时为 null |
| `totalSeconds` | 本客户端确认开工后的总时长秒数;首次接管已有任务时为 null |
| `manualReason`, `observedAt` | 人工处理原因和最近观测时间,可为 null |

轮询建议间隔不小于 1 秒。四个设施快照在状态锁内复制,不会将单次观察的字段读成新旧混合。
运行状态通常在调度周期(10 秒)或流程阶段变化时更新。HTTP 响应新鲜不代表游戏刚被重新观察;
使用 `observedAt` 判断游戏读数的时间。

PowerShell 只读验证:

```powershell
$key = Get-Clipboard
Invoke-RestMethod 'http://127.0.0.1:17890/api/v1/status' -Headers @{ Authorization = "Bearer $key" }
```

### POST /api/v1/action

另需桌面允许控制,以及 `Content-Type: application/json`。请求体示例:

```json
{"action":"pause"}
```

| action | 行为 | 成功状态码 |
| --- | --- | --- |
| `start` | 请求按当前计划执行一轮,不打开自动循环 | 202 |
| `stop` | 关闭自动循环并取消当前操作,不取消游戏中已开工的制造 | 202 |
| `pause` | 关闭自动循环,允许当前轮自然结束 | 200 |
| `resume` | 开启自动循环,由原调度器决定下次执行时机 | 200 |

`202` 代表已接受请求,不是执行成功。继续轮询状态并查看桌面日志确认结果。
所有游戏动作仍经过原有执行锁、校准检查、更新闸门及失败处理;不绕过游戏画面验证。
接口不提供取消单设施制造、修改物品或执行任意命令。

错误响应形如 `{"error":"unauthorized"}`;控制层拒绝返回 `{"message":"already_running"}`。
`400` 非法 JSON/动作,`401` 密钥错误,`403` 未允许控制,`404` 未知路由,
`405` 方法错误,`409` 正在运行/更新,`413` 请求体超过 1024 字节,
`415` 非 JSON,`500` 内部错误,`503` 超时/退出/并发超限。
最多同时处理 8 个请求,请求处理超时 5 秒。并发超限可返回无响应体的 `503`。

### POST /api/v1/settings

与动作接口共用配对鉴权、允许设备控制开关、JSON 格式和 1024 字节上限。
一次只保存一个设置,成功返回 `200 {"message":"saved"}`。请求示例:

```json
{"facility":"tech-center","enabled":true}
```

```json
{"facility":"workbench","craftMode":"HourlyProfit"}
```

全局请求分别使用 `autoLoopEnabled` (bool)、`steamDetectionEnabled` (bool) 或
`afterRun` (CloseGame / KeepRunning / KeepAtLobby)。设施模式为 Custom / HourlyProfit / TotalProfit。
不提供计划物品编辑。设置在任务之间保存,与 App 计划页共用保存和利润推荐逻辑。
非法字段组合返回 400,只读返回 403,更新进行中返回 409,等待任务结束超时返回 503。
Steam 显示状态每 30 秒独立查询,过期或失败显示不可用,不会改变任务的 5 分钟重试等待。

## 验证范围

CI 构建完整客户端和固件,执行 Core/API 及固件导航/显示桥接测试并上传 ZIP,覆盖
HTTP 鉴权、只读模式、动作分发、请求校验、状态契约、端口释放与在途请求取消。
Windows 实机 API 已通过局域网鉴权验证并返回四设施状态。
T-Display-S3 实机已完成烧录和 USB 配置,重启后成功连接 Wi-Fi 并持续获取四设施状态。
串口发送一行 `{"command":"info"}` 可检查固件的 Wi-Fi 与 API 在线状态,不返回凭据。
屏幕显示和实体按键仍需在设备上目视及实际操作确认。
