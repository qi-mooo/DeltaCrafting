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
若烧录后仍停在下载模式，按一下 RESET。配置时设备必须运行固件，关闭其他串口监视器。

桌面端在「设置 → T-Display-S3」开启 API 并复制配对密钥。电脑需允许本地子网访问
对应 TCP 端口。默认只读监控，按键控制另需开启「允许设备控制」。

## 从源码构建

需要 PlatformIO 6.1.19；板级定义和显示驱动使用固定版本的 LILYGO 仓库：

```bash
git clone https://github.com/Xinyuan-LilyGO/T-Display-S3.git .firmware-sdk
git -C .firmware-sdk checkout 5c7b97a42e6ed4ec299004f0578c097ece412d6f
export TDISPLAY_S3_DIR="$PWD/.firmware-sdk"
pio run -d firmware/t-display-s3
pio run -d firmware/t-display-s3 -t upload --upload-port /dev/cu.usbmodemXXXX
```

命令在 DeltaCrafting 仓库根目录执行。也可将 `TDISPLAY_S3_DIR` 指向已有的同版本
T-Display-S3 仓库。Windows PowerShell 使用 `$env:TDISPLAY_S3_DIR = '板级仓库绝对路径'`。
打包文件位于工程的 `.pio/build/deltacrafter-monitor/DeltaCrafter-esp32s3.zip`。
在发布包的 `source` 目录重建时，使用 `pio run -d .`。
工程拒绝带 `include/config.local.h` 的发布构建；请使用 USB 配置凭据。
默认使用新版 LCD 初始化，早期面板可在编译参数加入 `-DDELTA_LCD_NEW_PANEL=0`。

## 屏幕与按键

| 按键 | 短按 | 长按 1.5 秒（桌面需允许控制） |
| --- | --- | --- |
| GPIO 0 | 切换总览/四个设施详情 | 暂停或恢复自动循环 |
| GPIO 14 | 立即刷新 | 空闲时执行一轮；运行中停止并关闭自动循环 |

每 3 秒轮询；断线保留旧画面但显示 `STALE`，禁用倒计时和控制；无历史数据时显示
`OFFLINE`。HTTP 在独立任务中运行。控制超时显示结果未知并刷新，不会自动重试。

串口 115200 波特率发送一行 `{"command":"info"}` 可读取配置状态、Wi-Fi IP、
API 在线状态、设施数量和可用内存，不会返回密码或密钥。
明文 HTTP 仅用于可信局域网，不要做公网端口转发。
