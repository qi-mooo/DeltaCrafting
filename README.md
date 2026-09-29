<div align="center">
  <img src="src/DeltaCrafter.App/Assets/AppIcon.png" alt="DeltaCrafter 图标" width="128" height="128">

  <h1>DeltaCrafter</h1>

  <p>三角洲特勤助手：面向《三角洲行动》国服特勤处的 Windows 本地制造计划与自动循环工具。</p>

  <p>
    <a href="https://github.com/ixekico/DeltaCrafting/releases/latest">
      <img src="https://img.shields.io/github/v/release/ixekico/DeltaCrafting?display_name=tag&sort=semver" alt="最新版本">
    </a>
    <a href="https://github.com/ixekico/DeltaCrafting/actions/workflows/ci.yml">
      <img src="https://github.com/ixekico/DeltaCrafting/actions/workflows/ci.yml/badge.svg" alt="持续集成">
    </a>
    <a href="LICENSE">
      <img src="https://img.shields.io/badge/license-MIT-yellow.svg" alt="MIT 许可证">
    </a>
    <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4?logo=windows11" alt="支持 Windows 10 和 Windows 11">
  </p>

  <p>
    <a href="https://github.com/ixekico/DeltaCrafting/releases/latest"><strong>下载最新版</strong></a>
    ·
    <a href="https://github.com/ixekico/DeltaCrafting/issues/new?template=bug_report.yml">报告问题</a>
    ·
    <a href="https://github.com/ixekico/DeltaCrafting/issues/new?template=feature_request.yml">功能建议</a>
    ·
    <a href="CHANGELOG.md">更新日志</a>
  </p>
</div>

> [!CAUTION]
> DeltaCrafter 不是腾讯、琳琅天上或《三角洲行动》的官方产品，也未获得其认可。自动化工具可能受到游戏协议或运营规则限制。使用前请自行确认适用规则，并自行承担账号与资产风险。

> [!WARNING]
> **免责声明：**本软件开源免费，仅供学习交流，**请勿用于非法用途！** 作者不对使用本软件产生的任何后果负责。

## 目录

- [项目简介](#项目简介)
- [功能特性](#功能特性)
- [运行要求](#运行要求)
- [下载与安装](#下载与安装)
- [使用方法](#使用方法)
- [自动更新](#自动更新)
- [安全与隐私](#安全与隐私)
- [已知限制](#已知限制)
- [从源码构建](#从源码构建)
- [项目架构](#项目架构)
- [参与贡献](#参与贡献)
- [许可证](#许可证)
- [致谢](#致谢)

## 项目简介

DeltaCrafter 用于按照预先设定的计划管理《三角洲行动》国服特勤处制造循环。它可以启动游戏、进入特勤处、检查四个制造设施、领取完成品、按计划续造，并根据游戏画面中的倒计时安排下一轮执行。

整个过程只使用屏幕截图、离线 OCR 和系统模拟输入，不读取游戏内存，也不调用游戏内部接口。物品列表、详情标题与当前任务使用随客户端分发的 PaddleOCR V5 中文模型；界面导航和操作按钮使用 Windows 简体中文 OCR。设置、制造计划、运行状态、日志和失败截图均保存在本机。

当前开发版本为 `0.4.0`。2560×1440、16:9 无边框窗口下的核心链路已经完成实机验证；1920×1080 已完成关键页面与状态的 OCR、锚点回放验证。详细变更与验证结果请查看 [CHANGELOG.md](CHANGELOG.md)。

## 功能特性

- **制造循环：**自动进入特勤处、识别四个设施、领取完成品并按计划续造。
- **独立制造模式：**每个设施都能分别设置为自定义物品、每小时利润最高或总利润最高。
- **行情推荐：**「每小时利润最高」只在设施真正开工前查询三角洲数据帝，或在 S3 设施设置中手动「刷新利润物品」。自定义物品不查询利润，启动、制造过程中与倒计时归零均不额外查询。
- **刷新数据：**在电脑设置页填写数据帝 Token 和设施等级，点击「刷新数据」更新四设施可制造物品列表；S3 全局菜单也提供该按钮。此操作不更新利润推荐、不改变计划。目录仅在四设施数据全部成功后替换，浏览列表与状态轮询不查询数据帝。
- **物品等级：**电脑与 S3 的物品列表使用数据帝 `grade` 显示等级，例如「5级 .300BLK」；原始物品身份仍用于保存和识别。旧目录缺少等级时显示「等级未知」。当前制造接口不返回产出数量，不推测数量。
- **数据帝工具：**Windows 和 S3 均提供今日密码、当前集市物品、改枪码列表与详情。进入具体工具、手动刷新或改枪码翻页时才查询；工具选择菜单、浏览详情和复制不重复查询。改枪码使用 `gun_gqm_v2`，可切换烽火/大战场并复制到 Windows 剪贴板；Windows 还可搜索枪械或方案名称。
- **自定义记忆：**利润模式只改变当前推荐；切回自定义模式会恢复该设施最后一次手动选择的物品。
- **材料补齐：**材料不足时可执行游戏内“一键补齐”；仓库空间不足时会停止当前流程并提醒用户清理仓库。
- **可靠识别：**关键状态使用新截图验证，同一设施需要多次 OCR 结果形成共识，无法读取倒计时不会被误判为制造完成。
- **自动调度：**依据游戏内倒计时安排下一次执行，支持取消、失败退避、防睡眠和托盘运行。
- **自动更新：**启动时检查 GitHub Releases，在更新窗口展示精简的新版功能与修复说明，校验 SHA-256 后执行覆盖安装。
- **桌面体验：**支持深色、浅色和跟随系统主题，窗口标题直接显示当前版本号。
- **T-Display-S3：**内置带配对密钥的局域网 API，配套固件显示四设施状态、中文物品与倒计时，可选按键控制。参见 [接入与烧录指南](docs/T-Display-S3接入指南.md)。

### 制造模式

| 模式 | 行为 |
| --- | --- |
| 自定义物品 | 使用该设施最后一次手动选择的物品 |
| 每小时利润最高 | 使用当前行情中该设施单位时间利润最高的物品 |
| 总利润最高 | 保留已有选择，不自动刷新行情 |

制造模式和启用状态均按设施单独保存。行情推荐不会覆盖保存的自定义选择。

## 运行要求

- Windows 10 版本 2004（内部版本 19041）或更高，推荐 Windows 11
- x64 处理器
- Windows 简体中文 OCR 组件
- 16:9 无边框游戏窗口，例如 1920×1080 或 2560×1440
- 管理员权限

Release 中的安装包和免安装压缩包均为自包含版本，普通用户无需另外安装 .NET 运行时。

如系统缺少简体中文 OCR，请前往：

> Windows 设置 → 时间和语言 → 语言和区域 → 中文（简体）→ 语言选项 → 安装“光学字符识别”

## 下载与安装

所有正式版本均发布在 [GitHub Releases](https://github.com/ixekico/DeltaCrafting/releases)。请只从本仓库下载，并同时取得对应的 `.sha256` 文件。

### 安装包（推荐）

1. 下载 `DeltaCrafter-Setup-<版本>.exe` 和 `DeltaCrafter-Setup-<版本>.exe.sha256`。
2. 校验文件的 SHA-256。
3. 运行安装程序并授予管理员权限；可以选择创建桌面快捷方式。
4. 安装完成后直接启动 DeltaCrafter。

卸载入口位于 Windows“设置 → 应用”。卸载程序会先停止 DeltaCrafter，并删除应用创建的“开机自启”计划任务，然后询问是否同时删除本机数据；默认保留制造计划、设置、日志和诊断截图。

静默卸载始终保留本机数据：

```powershell
& "$env:ProgramFiles\DeltaCrafter\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

### 免安装压缩包

1. 下载 `DeltaCrafter-win-x64-<版本>.zip` 和 `DeltaCrafter-win-x64-<版本>.zip.sha256`。
2. 校验文件的 SHA-256。
3. 解压到普通可写目录，不要直接在压缩包中运行。
4. 启动 `DeltaCrafter.exe`，并按系统提示授予管理员权限。

以 `0.4.0` 免安装包为例：

```powershell
Get-FileHash .\DeltaCrafter-win-x64-0.4.0.zip -Algorithm SHA256
Get-Content .\DeltaCrafter-win-x64-0.4.0.zip.sha256
```

两处哈希值应完全一致。应用暂未进行商业代码签名，Windows SmartScreen 首次运行时可能显示“未知发布者”。

## 使用方法

1. 打开“设置”，选择启动方式：普通启动器选择游戏启动器或可执行文件；Steam 版选择“Steam (本机游戏)”，默认 App ID 为 `2507950`，客户端路径可自动检测或手动指定 `steam.exe`。
2. 手动打开游戏，通过“定位窗口”绑定正确的窗口标题和窗口类名。
3. 在“制造计划”中为四个设施分别选择制造模式、物品和启用状态。
4. 如需接管游戏中已经进行的制造，使用“识别当前任务”读取剩余时间。
5. 首次自动运行前，建议打开开发者模式，依次验证“启动到大厅 → 进入特勤处 → 识别画面”。
6. 确认识别与点击位置正确后，再启动自动循环。

默认锚点按 2560×1440 标定，并使用归一化坐标适配其他 16:9 分辨率。游戏更新后如出现识别失败或点击偏移，请参照 [构建与校准指南](docs/构建与校准指南.md) 检查和调整。

制造 `.300 BLK` 五级弹时，计划中仍选择 `.300BLK五级弹`。该条目单独按游戏名称 `.300 BLK`
与左侧金色图标背景识别，点击后确认该行选中框和详情标题；无法确认时停止。
其他物品（包括 `.300BLK SUB-3`、`SUB-4`）继续使用原有文字匹配。
此规则已通过截图回放和单元测试，尚需 Windows 游戏内验证。

Steam 模式会读取本机 Steam 库和游戏安装清单，通过本机 `steam.exe -applaunch` 启动，并核对窗口所属进程是否位于游戏安装目录。仅可通过 Remote Play 串流的游戏不视为已安装，串流窗口也不会被助手接管；请先在本机安装游戏并登录 Steam。安装检测不能代替 Steam 的文件完整性验证，也不保证绕过 Steam 自身的登录、更新或启动选项对话框。

新版模式选择页会点击左下角第一张「烽火地带」卡片的「前往游玩」，同时核验卡片模式名称，随后等待公告、基地或大厅画面。若停在「退出游戏」菜单，会先按 ESC 返回。锚点修订号 10 在启动时自动备份并升级旧锚点，旧版模式入口仍保留兼容。

其他 Steam 发行版本可填写商店网址 `/app/` 后的 App ID。游戏应设为简体中文、16:9 无边框窗口；启动支持不代表不同发行版本的特勤处画面与国服锚点完全一致，首次使用请按上述单步流程验证。切换版本后若仍绑定旧窗口，请重置或重新定位游戏窗口。

开发构建可从 GitHub Actions 的 `CI` 运行页面下载 `DeltaCrafter-win-x64-<提交哈希>` artifact，内含自包含 ZIP 和 SHA-256 校验文件；完整解压 ZIP 后运行 `DeltaCrafter.exe`。CI 在 Windows 上完成编译、单元测试与打包，支持手动运行。

同一次 CI 还提供 `DeltaCrafter-esp32s3-<提交哈希>` 固件 artifact，包含分区镜像、校验清单、烧录和 USB 配置工具。Wi-Fi 与配对密钥通过 USB 写入设备，不包含在公开固件中。参见 [固件说明](firmware/t-display-s3/README.md)。CI 只由 `main` / `codex/**` 推送或手动触发，避免同一次推送因已开 PR 再运行一遍。

### Steam 游戏状态检测

在「设置 → Steam 游戏状态」填写 Steam Web API key、17 位 SteamID64，并开启「游戏中暂停任务」。
每次任务开始前调用 HTTPS `ISteamUser/GetPlayerSummaries/v0002`，账号正在运行三角洲行动时暂缓任务，其他游戏忽略。
优先匹配 App ID `2507950`，未返回 ID 时仅匹配完整名称 `Delta Force` 或 `三角洲行动`。
5 分钟后由自动循环重新检查；查询超时、失败或资料不可见也采用相同等待。自动循环关闭时不会自动启动任务，
手动/托盘/S3 触发同样遵守等待时间。此等待不覆盖制造读数和上次执行结果，也不会最小化窗口或操作游戏。

启用检测后不再提前启动游戏，避免助手预启动造成自己的任务被拦截。Steam 仅提供账号上报的游戏状态，
无法区分大厅挂机和实际对局。助手记录自己启动的游戏进程 ID、启动时间和可执行路径，
同一个进程仍运行时允许继续任务；本机手动启动仍暂缓。游戏退出或重启后自动失去放行资格，
助手重启后也不会凭已有游戏窗口认领进程。
总览页和 S3 全局菜单均提供「关闭游戏」，可在 Steam 暂缓时关闭本机游戏（包括最小化窗口）；
任务执行中先停止再关闭。此按钮不启动游戏、不改变设施进度，自动循环仍按原计划工作。
请将 Steam 个人资料和游戏详情设为公开；游戏详情隐藏时，接口可能不返回游戏字段，无法可靠检测。
设置中的「检查 Steam 状态」可立即查询，不启动任务。凭据仅保存在本机设置，发布包中不包含个人 key。

## 自动更新

程序每次启动时自动检查一次更新，也可以在“设置 → 关于”中手动检查。

发现新版本后，更新窗口会先展示该版本面向用户的新增功能与修复说明；完整的技术记录仍保留在 [CHANGELOG.md](CHANGELOG.md)。用户确认后，程序将从 GitHub Releases 下载官方安装包并校验 SHA-256；只有校验通过才会静默覆盖安装并重新启动。校验失败时会删除下载文件并明确报错。

更新过程中会暂停自动调度，但不会清除制造计划、当前计时进度或设置。窗口驻留托盘时，程序会先发送系统通知，等待用户打开窗口后再决定是否更新。

## 安全与隐私

DeltaCrafter 将“避免错误输入”置于“尽量继续运行”之前：

- 每次关键点击后重新截图，确认已经进入预期界面。
- 单次 OCR 不直接定案，同一设施需要多次结果形成共识。
- 先按当前目标筛选 OCR：规范化后整串匹配度严格超过 60% 才检查 90% 置信度门槛；≤60% 的无关文字、符号和乱码直接忽略。匹配度仅用于筛选，完整名称仍必须唯一命中目录，制造前再次核对详情标题。.300 BLK 同时校验蓝／紫／金品质颜色，区分三级、四级和五级弹。倒计时未读清时重试观察，不据此判为制造完成。
- PaddleOCR 模型和原生库随完整发布包离线提供，首次识别需要加载模型。请完整解压；缺失模型或运行库时明确报错，不自动降级猜测物品。
- 无法解析倒计时不等同于制造完成。
- 校验失败只进行一次明确重试，随后中止本轮并保留现场。
- 配置损坏、OCR 缺失、材料不足或仓库已满都会明确提示，不会静默跳过。

所有用户数据均保存在：

```text
%LocalAppData%\DeltaCrafter
```

程序不包含遥测。开启设备 API 后，会向持有配对密钥的局域网客户端提供运行状态与制造计划摘要；API 默认关闭，远程控制需另行开启。提交问题前，请检查日志和失败截图是否包含账号昵称、聊天内容、Windows 用户名或其他个人信息。安全问题请按照 [SECURITY.md](SECURITY.md) 私下报告。

## 已知限制

- 2560×1440 已完成端到端实机验证；1920×1080 已使用 14 个关键页面和状态完成 OCR 与锚点回放，其中包含游戏客户区 1919×1080 的一像素偏差。其他 16:9 分辨率仍需更多样本。
- 游戏 UI、字体或 OCR 行为变化后，可能需要更新 `anchors.json` 或文字最小化折叠规则。
- 电脑必须保持唤醒；防睡眠功能不能代替关机或休眠后的唤醒任务。
- 当前只发布 Windows x64 版本。
- Setup 和免安装压缩包均未进行商业代码签名。

## 从源码构建

### 开发环境

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 支持 WinUI 3 的 Windows 开发环境
- [Inno Setup 6](https://jrsoftware.org/isinfo.php)（仅构建安装包时需要）

### 构建与测试

```powershell
dotnet restore DeltaCrafter.sln --configfile nuget.config
dotnet build DeltaCrafter.sln -c Release -p:Platform=x64 --no-restore
dotnet test tests\DeltaCrafter.Core.Tests\DeltaCrafter.Core.Tests.csproj -c Release -p:Platform=x64 --no-build --no-restore
```

### 生成发布文件

```powershell
.\scripts\build-release.ps1 -Version 0.4.0
.\scripts\install-inno-setup.ps1
.\scripts\build-installer.ps1 -Version 0.4.0 -SkipBuild
```

安装包构建固定使用官方签名的 Inno Setup 6.7.3，并校验编译器、简体中文语言文件和复用 payload 的版本。任何输入缺失或版本不一致都会中止构建，不会生成降级安装包。

## 项目架构

DeltaCrafter 使用 C#、.NET 8、WinUI 3 和 Windows App SDK 构建。核心依赖保持单向：

```text
L3 编排层 → L2 流程层 → L1 能力组件 → L0 领域模型
UI 只消费 L3 与 L0
```

```text
src/
├─ DeltaCrafter.App/       WinUI 3 界面与桌面集成
└─ DeltaCrafter.Core/
   ├─ L0/                  领域模型与不变量
   ├─ L1/                  OCR、截图、输入、存储等单一能力
   ├─ L2/                  制造与导航流程
   └─ L3/                  自动化、行情与更新编排
tests/                     核心逻辑测试
scripts/                   构建、安装包与发布说明脚本
installer/                 Inno Setup 工程
docs/                      构建与画面校准文档
```

## 参与贡献

欢迎通过 Issue 报告可复现的问题，或提交 Pull Request 改进项目。开始前请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md)，并确保修改符合分层约束及“失败必须明确”的原则。

- [报告 Bug](https://github.com/ixekico/DeltaCrafting/issues/new?template=bug_report.yml)
- [提出功能建议](https://github.com/ixekico/DeltaCrafting/issues/new?template=feature_request.yml)
- [安全问题报告](SECURITY.md)

## 许可证

本项目采用 [MIT 许可证](LICENSE)。你可以使用、复制、修改、合并、发布和分发本软件，但必须保留原版权声明与许可证文本。第三方组件仍分别适用其上游许可证，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 致谢

- 配方与行情数据来源：[三角洲数据帝](https://work-api.apifox.cn/)
- README 结构参考：[Best-README-Template](https://github.com/othneildrew/Best-README-Template)、[readme-template](https://github.com/iuricode/readme-template) 与 [standard-readme](https://github.com/RichardLitt/standard-readme)
- 直接依赖及其许可证见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
