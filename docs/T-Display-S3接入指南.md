# T-Display-S3 接入指南

DeltaCrafter 内置 HTTP API,供同一局域网的 LILYGO T-Display-S3 显示四个制造设施、
当前物品、倒计时及运行状态。API 随桌面应用运行,无需另起服务。USB 用于供电/烧录;
设备通过 2.4 GHz Wi-Fi 与电脑通信。

## 桌面端配对

1. 打开 **设置 → T-Display-S3**,开启「局域网设备 API」。默认端口为 `17890`。
2. 点击配对密钥的复制按钮,保存到下方固件配置。首次启用自动生成 64 位随机密钥。
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
重新生成密钥会重新启动监听,旧密钥立即失效,需同步更新并烧录固件。

## 固件配置与烧录

配套工程位于同级 `T-Display-S3` 仓库的 `examples/DeltaCrafterMonitor/`,
使用该仓库的板级定义、TFT_eSPI 驱动和中文字库。它有独立的 `platformio.ini`,
不修改或依赖根工程的默认固件选择。

```bash
cd /Users/qimo/Documents/code/T-Display-S3
cp examples/DeltaCrafterMonitor/include/config.local.example.h examples/DeltaCrafterMonitor/include/config.local.h
```

编辑 `config.local.h`:

```cpp
#pragma once
#define DELTA_WIFI_SSID "Your-WiFi"
#define DELTA_WIFI_PASSWORD "Your-WiFi-Password"
#define DELTA_BASE_URL "http://192.168.1.100:17890"
#define DELTA_API_KEY "从桌面设置复制的64位密钥"
#define DELTA_POLL_MS 3000
#define DELTA_LCD_NEW_PANEL 1
```

`DELTA_BASE_URL` 只包含电脑地址和端口;不要填 `localhost` 或 `/api/v1/status`。
SSID 留空可复用设备 NVS 中已有的 Wi-Fi 配置。配置文件已被 `.gitignore` 排除,
但生成的固件包含 Wi-Fi 密码和配对密钥,不要公开分享个性化固件。

安装 PlatformIO 后,从 T-Display-S3 仓库根目录执行:

```bash
pio run -d examples/DeltaCrafterMonitor
pio run -d examples/DeltaCrafterMonitor -t upload --upload-port /dev/cu.usbmodemXXXX
pio device monitor -b 115200 --port /dev/cu.usbmodemXXXX
```

本机也可用 `.venv/bin/pio` 或 `~/.platformio/penv/bin/pio`。Windows 串口填写 `COM5`
等实际端口。烧录前确认选择的是目标 S3 设备;自动下载失败时按住 BOOT,按一下 RESET,
然后松开 BOOT 再试。只编译不会覆盖现有固件。

固件文件为 `examples/DeltaCrafterMonitor/.pio/build/deltacrafter-monitor/firmware.bin`。
这是应用分区镜像,不是可直接写到地址 0 的合并镜像;推荐使用上述 PlatformIO 上传命令。
默认使用新版 LCD 初始化;早期屏幕显示异常时可将 `DELTA_LCD_NEW_PANEL` 设为 `0` 后重编译。

## 屏幕与按键

| 操作 | 行为 |
| --- | --- |
| GPIO 0 短按 | 总览/工作台/制药台/防具台/技术中心之间切换 |
| GPIO 14 短按 | 立即刷新 |
| GPIO 0 长按 1.5 秒 | 暂停/恢复自动循环 |
| GPIO 14 长按 1.5 秒 | 空闲时立即执行一轮,执行中停止任务并关闭自动循环 |

长按控制仅在数据新鲜、网络在线且桌面允许控制时生效。控制请求不会自动重试;
请求超时时显示结果未知并刷新状态,避免重复操作。

默认每 3 秒获取一次状态,屏幕每 250 ms 绘制,倒计时按设备本地单调时钟推算,无需 NTP。
API 返回的完成时刻来自游戏 OCR。即使倒计时归零,仍保留服务器观测的阶段,
不会凭计时将「制造中」改成「可领取」。禁用计划的设施仍显示已观测的任务。
网络错误时保留旧数据并标记 `STALE`,停止显示旧倒计时且禁用控制;从未成功连接显示 `OFFLINE`。
网络任务独立运行,重连不阻塞按键。

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

| 设施字段 | 含义 |
| --- | --- |
| `key` | workbench / pharmacy-lab / armor-station / tech-center |
| `name`, `enabled` | 中文名称、计划是否启用 |
| `craftMode`, `plannedItemName` | Custom/HourlyProfit/TotalProfit、计划物品 |
| `phase` | Unknown/Idle/Crafting/ReadyToCollect/NeedsManual |
| `itemName` | 游戏中最近观测的当前物品,与计划物品分开 |
| `readyAt`, `remainingSeconds` | OCR 完成时间和非负剩余秒数;无有效制造倒计时为 null |
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

## 验证范围

2026-09-28 在 macOS 上通过 156 项 Core/API 测试,覆盖真实 HTTP 请求的鉴权、只读模式、
动作分发、请求校验、状态契约、端口释放与在途请求取消。PlatformIO 成功生成 ESP32-S3
固件,约 1.1 MB。完整桌面构建因 Windows 专用 `XamlCompiler.exe` 无法在 macOS 运行而
未完成;XAML 已通过 XML 语法检查。尚未烧录实体设备,Windows 界面、局域网防火墙、
屏幕显示及按键操作仍需实机联调。
