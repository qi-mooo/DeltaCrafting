# 第三方组件声明

子弹量化的均线、RSI、区间位置与波动率算法，以及策略评分改编自
[leitingquant](https://github.com/qi-mooo/leitingquant) 的 `WindowSpy/QuantMath.cs` 与 `WindowSpy/MarketService.cs`，
固定来源提交 `dbdd8fb66719f63d67d5c1bd5175fadd52c4e9a7`。
原作者为雷霆网络开发工作室，使用 MIT 许可证，全文随包保留在
`licenses/leitingquant-LICENSE.txt`。数据源替换为三角洲数据帝，未引入 LLM 依赖。

DeltaCrafter 直接使用以下 NuGet 软件包。其版权与许可证归各自权利人所有。

| 组件 | 版本 | 许可证 | 项目 |
|---|---:|---|---|
| Microsoft.WindowsAppSDK | 1.7.250606001 | Microsoft Windows App SDK License Terms | <https://github.com/microsoft/WindowsAppSDK> |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | <https://github.com/CommunityToolkit/dotnet> |
| H.NotifyIcon.WinUI | 2.3.0 | MIT | <https://github.com/HavenDV/H.NotifyIcon> |
| Serilog | 4.2.0 | Apache-2.0 | <https://github.com/serilog/serilog> |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 | <https://github.com/serilog/serilog-sinks-file> |
| System.Text.Encoding.CodePages | 8.0.0 | MIT | <https://github.com/dotnet/runtime> |
| Sdcb.PaddleOCR / local models | 3.3.1 | Apache-2.0 | <https://github.com/sdcb/PaddleSharp> |
| Sdcb.PaddleInference runtime (MKL) | 3.3.1.70 | Apache-2.0 | <https://github.com/sdcb/PaddleSharp> |
| OpenCvSharp4 runtime | 4.11.0.20250507 | Apache-2.0 | <https://github.com/shimat/opencvsharp> |

测试工程还使用：

| 组件 | 版本 | 许可证 |
|---|---:|---|
| Microsoft.NET.Test.Sdk | 17.11.1 | MIT |
| xunit | 2.9.2 | Apache-2.0 |
| xunit.runner.visualstudio | 2.8.2 | Apache-2.0 |

发布工程使用 Inno Setup 6.7.3 编译安装包。仓库内的
`installer/Languages/ChineseSimplified.isl` 来自 Inno Setup 官方源码仓库
`is-6_7_3` 标签，文件头保留翻译维护者 Zhenghan Yang（Kira）的声明；来源、固定校验值
与分发条款链接见 `installer/Languages/README.md`。Inno Setup 编译器不随应用发布。

自包含发布目录中会包含 Windows App SDK 运行组件及其上游依赖。对应的许可证正文和上游声明位于发布包的 `licenses/` 目录；本文件不替代这些正文。
