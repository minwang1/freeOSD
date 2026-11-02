# Monitor Control

跨平台显示器控制桌面应用，采用 **C#、.NET 10 和 Avalonia**。根目录是唯一活动项目；旧 `python/` 目录不属于开发或验证范围，用户计划后续删除。迁移计划见 [`plan.md`](plan.md)，UI 规格见 [`UI-DESIGN.md`](UI-DESIGN.md)。

## 当前状态

- Avalonia 主窗口和基础控制页已建立。
- UI 使用 TKCode.Arc 0.3.2-preview.2 原生 Avalonia 控件与设计 token；设置页支持中文/英文即时切换、应用版本显示、跟随系统/浅色/深色主题，以及浅蓝、浅绿、浅橘或黑色强调色。语言、主题和强调色保存在用户本地配置中；语言覆盖导航、各功能面板、状态提示、对话框和托盘菜单。强调色会应用到控件状态并自动适配深浅背景。按钮、下拉选择、滑块和开关统一 Arc 交互样式。Avalonia FluentTheme 保留给 Windows 托盘右键弹出菜单等原生控件提供默认模板。Arc 仍处于 preview，版本固定以避免浮动升级。
- Windows DXVA2 DDC/CI 后端初版支持显示器发现、能力读取、常用 VCP 读取与写入。
- Windows 显示设置页将分辨率与刷新率分开选择和应用；分辨率切换保持当前刷新率，刷新率切换保持当前分辨率，两项均采用 15 秒试用、确认保留或自动恢复。
- 左侧“色彩管理”面板提供 Windows ICC profile 和 RGB SDR 校准操作。可导入 `.icc`/`.icm` 并关联当前显示器；系统安装 profile 需要管理员权限。RGB 滑块停止调整后会尝试使用临时 Gamma Ramp 预览，不生成文件；点击“应用 RGB 调整”才会弹出可编辑文件名的保存确认，并生成独立 ICC/VCGT profile。临时预览在应用、HDR 开启、切换显示器或退出时尝试恢复原 ramp；具体效果取决于显卡驱动，Windows 可能忽略或重置该 ramp。每通道范围 50–150%，HDR 开启时禁止应用。首次应用保存原始 profile，失败自动回滚，支持手动恢复。RGB 可见变化已在一台 Windows 显示器上由用户确认；其他显示器和 HDR 尚未验证。ICC 列表旁可删除单个未激活的 Monitor Control RGB profile，活动项和备份中的原始 profile 受保护。只读摘要会校验 ICC 文件头和标签边界，并显示 VCGT/MS00/MHC2 结构信息。面板还报告 Windows VCGT/MHC2 能力、系统校准加载器状态和 HDR 状态。仓库提供[导入流程样例](docs/test-profile/test-srgb.icm)，它不含校准曲线，只用于检查导入、关联和恢复流程。
- 控制面板只显示常用画面与输入源控件；亮度和对比度滑块占满剩余行宽，不重复显示右侧数值；VCP RGB 增益已隐藏，RGB 校准统一放在“色彩管理”面板。
- 控制能力读取、VCP 写入、写后读回、分辨率/刷新率模式验证、应用及恢复结果会写入本地诊断日志，路径为 `%LOCALAPPDATA%\MonitorControl\Logs\monitor-control.log`；日志按大小轮转，不包含原始显示器能力转储。DDC/CI 读回暂时失败时会有限重试；仍无法确认的值会标记为未知。
- Linux DDC/CI 后端尚未实现。Linux 上的 UI 会显示当前平台后端尚不可用。
- C# 解决方案已通过当前环境的构建（0 警告、0 错误），Avalonia 编译绑定通过。DDC/CI 写入和刷新率实际切换/回滚尚未在真实显示器上验证，暂不视为可发布版本。
- Windows App 已在当前开发机启动并显示主界面，成功枚举到 Generic PnP Monitor、读取显示器能力和当前分辨率的可用刷新率；本次未验证硬件写入或切换效果。

## 项目结构

```text
src/MonitorControl.App/                 Avalonia UI 与 MVVM
src/MonitorControl.Core/                跨平台模型、契约和用例
src/MonitorControl.Platform.Windows/    Windows DXVA2/DDC/CI 后端
```

Core 契约边界和安全约定见 [`docs/core-contracts.md`](docs/core-contracts.md)。
显示器图像模式、色温和 DDC/CI 故障排查见[常见问题](docs/faq.md)。
Windows GPU 色彩校正（ICC/VCGT/MHC2）的技术评估见 [`docs/windows-gpu-color-correction-spike.md`](docs/windows-gpu-color-correction-spike.md)。

## 开发环境与命令

需要已安装 .NET 10 SDK。当前已在 Windows 开发环境完成解决方案构建，并运行 Core 契约检查：

`AVALONIA_TELEMETRY_OPTOUT=1`（PowerShell 中 `$env:AVALONIA_TELEMETRY_OPTOUT = '1'`）用于禁用构建期遥测写入；`-m:1` 用于串行构建。

```powershell
$env:AVALONIA_TELEMETRY_OPTOUT = '1'
dotnet build .\MonitorControl.sln --no-restore -m:1 -v:minimal
dotnet run --project .\tests\MonitorControl.Core.Tests\MonitorControl.Core.Tests.csproj
dotnet run --project .\src\MonitorControl.App\MonitorControl.App.csproj
```

最近一次构建结果：0 警告、0 错误。Core 契约检查为 5/5 通过。检查项目目前是无第三方测试依赖的 C# 控制台检查器，不应使用 `dotnet test` 作为其运行命令。Windows 主窗口启动和能力读取已实机运行验证，但未尝试写入控制值；Linux 构建和运行尚未验证。

DDC/CI 实机功能需要支持 DDC/CI 的显示器和连接链路，并在显示器 OSD 中启用 DDC/CI。显示器写入会立即生效；输入源切换会先要求确认。

## 平台范围

Windows 是当前首个硬件后端目标。Linux UI 与后端接口按跨平台结构设计，但 Linux 设备发现、DDC/CI 权限、托盘、自启动、刷新率和 HDR 支持均需分别实现和验证；Avalonia 可启动不代表这些系统功能已受支持。

Windows 登录启动可在“设置”页启用，作用于当前用户且无需管理员权限；Linux 登录启动尚未实现。

Windows HDR 使用 HDR 专用 DisplayConfig 状态查询与切换，并在操作后读回状态。旧版 Advanced Color 位同时覆盖 HDR 与 WCG，不能用来判断 HDR，因此 HDR 专用接口不可用时不报告支持。显卡、驱动、Windows 版本、远程会话和连接方式可能限制该能力；该功能仍需在目标 Windows/显示器组合上进行实机验证。

Windows“预设与自动化”支持从已安装程序列表创建前台应用规则，也可在添加规则时使用上一个前台程序，并为未命中规则的程序选择默认预设。规则、默认预设和自动切换开关状态保存在当前用户数据目录；首次运行默认关闭，后续启动恢复上次状态。显示器预设可保存当前活动 ICC profile，或本应用 RGB profile 中的 SDR 校准参数；自动切换未命中程序规则时同样应用默认预设中的颜色设置。ICC 应用要求该文件仍安装并关联到目标显示器；RGB 参数仅在目标显示器身份匹配、HDR 关闭且 VCGT 能力可用时应用。ICC 列表旁可删除单个符合本应用标识的 RGB profile，活动项与保存的原始 profile 受保护。自动应用逐项读回验证并跳过输入源设置。Linux 前台应用监控和 RGB ICC 校准尚未实现。
