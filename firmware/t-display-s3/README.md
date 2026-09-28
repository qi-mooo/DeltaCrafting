# DeltaCrafter Monitor

LILYGO T-Display-S3 固件，通过 Wi-Fi 显示 DeltaCrafter 四个制造设施、中文物品名、
制造倒计时与自动循环状态。使用桌面客户端内置的配对 API。

## 使用 CI 固件

在 GitHub Actions 的 CI 页面下载 `DeltaCrafter-esp32s3-<提交哈希>` artifact，
解压其中的 `DeltaCrafter-esp32s3.zip`。包内包括各分区镜像、烧录地址与 SHA-256 清单、
烧录和配置工具、ELF 及固件源码。固件不包含个人 Wi-Fi 密码或配对密钥。

安装 Python 3 后，在解压目录执行：

```bash
python -m pip install esptool==4.5.1 pyserial==3.5
python flash.py --port COM5
python configure.py --port COM5 --wifi-ssid Your-WiFi --base-url http://192.168.1.100:17890
```

Windows 端口使用实际的 `COM` 名称；macOS 使用 `/dev/cu.usbmodemXXXX`。
配置工具会隐藏输入 Wi-Fi 密码和桌面配对密钥，通过 USB 保存到板上 NVS，保存后自动重启。
以后更换网络、电脑地址或密钥只需重新运行配置工具。需要 2.4 GHz Wi-Fi。

烧录工具先验证镜像校验和，然后按清单写入对应分区，不执行整片擦除。不要把
`firmware.bin` 写到地址 0。进入下载模式：按住 BOOT，按一下 RESET，再松开 BOOT；
烧录完成后按一下 RESET 运行；工具不自动切换下载/运行模式。配置时设备必须运行固件，关闭其他串口监视器。

桌面端在「设置 → T-Display-S3」开启 API 并复制配对密钥。电脑需允许本地子网访问
对应 TCP 端口。默认只读监控，按键控制另需开启「允许设备控制」。

## 从源码构建

需要 PlatformIO 6.1.19；板级定义和显示驱动使用固定版本的 LILYGO 仓库：

```bash
git clone https://github.com/Xinyuan-LilyGO/T-Display-S3.git .firmware-sdk
git -C .firmware-sdk checkout 5c7b97a42e6ed4ec299004f0578c097ece412d6f
export TDISPLAY_S3_DIR="$PWD/.firmware-sdk"
pio run -d firmware/t-display-s3
python firmware/t-display-s3/tests/run_native.py
```

命令在 DeltaCrafting 仓库根目录执行。也可将 `TDISPLAY_S3_DIR` 指向已有的同版本
T-Display-S3 仓库。Windows PowerShell 使用 `$env:TDISPLAY_S3_DIR = '板级仓库绝对路径'`。
打包文件位于工程的 `.pio/build/deltacrafter-monitor/DeltaCrafter-esp32s3.zip`。
在发布包的 `source` 目录重建时，使用 `pio run -d .`。
工程拒绝带 `include/config.local.h` 的发布构建；请使用 USB 配置凭据。
默认使用新版 LCD 初始化，早期面板可在编译参数加入 `-DDELTA_LCD_NEW_PANEL=0`。

## 屏幕与按键

所有页面使用黑底白字。主界面按游戏中的位置排列：左上技术中心、右上工作台、
左下制药台、右下防具台，底部是游戏状态和电池图标。选中项使用粗双框和角标。
UI 使用固定版本 Axeuh_UI，焦点平滑移动和伸缩，进入/返回页面有滑动过渡，目标刷新率 60 FPS。
每格底部显示制造进度条：由客户端确认开工时间与 OCR 剩余时间计算；接管已有任务、
总时长未知时显示活动条，不猜测比例。离线时冻结进度，可领取时填满。
电池图标按 GPIO 4 校准电压显示四格电量，不显示百分比；无效读数显示叉号。
电压是近似电量，USB 充电时可能偏高，硬件无法可靠识别所有未接电池情况。

| 按键 | 单击 |
| --- | --- |
| GPIO 0（BOOT） | 下一项，循环选择 |
| GPIO 14 | 进入选中菜单 / 确认 |

按住只触发一次，无长按功能。开机时按住 BOOT 不会误触菜单。

主页选择顺序：技术中心 → 工作台 → 制药台 → 防具台 → 状态栏。
选中设施后按确认打开启用/停用和制造模式菜单；计划物品仍在 App 本体设置。
选中状态栏后按确认打开自动循环、Steam 游戏检测和收取后行为菜单。每个菜单都有返回项。
打开菜单不需要控制权限，保存需要电脑开启「允许设备控制」；设置成功后从 API 刷新确认，
超时不自动重试。确认主页条目不会直接启动或停止任务。

每 3 秒轮询；断线保留四格旧数据，停止倒计时，底部显示连接中断、游戏状态未知。
游戏状态来自客户端独立的 Steam 查询，每 30 秒更新，不使用“自动任务运行”代替。
旧客户端缺少状态字段时显示游戏状态查询中，并提示升级后才能保存设置。
HTTP 在独立任务中运行，游戏查询失败或过期不会显示为未在游戏。

串口 115200 波特率发送一行 `{"command":"info"}` 可读取配置状态、Wi-Fi IP、
API 在线状态、设施数量、固件标识 `axeuh-keys-v2`、按键映射和可用内存，不会返回密码或密钥。
发送 `{"command":"screen"}` 可读取当前画面：一行 JSON 头后紧跟 108800 字节 RGB565 小端数据。
明文 HTTP 仅用于可信局域网，不要做公网端口转发。
