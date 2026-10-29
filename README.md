# Monitor Control

跨平台显示器控制桌面应用，采用 **C#、.NET 10 和 Avalonia**。根目录是唯一活动项目；旧 `python/` 目录不属于开发或验证范围，用户计划后续删除。迁移计划见 [`plan.md`](plan.md)，UI 规格见 [`UI-DESIGN.md`](UI-DESIGN.md)。

## 当前状态

- Avalonia 主窗口和基础控制页已建立。
- Windows DXVA2 DDC/CI 后端初版支持显示器发现、能力读取、常用 VCP 读取与写入。
- Windows 显示设置页将分辨率与刷新率分开选择和应用；分辨率切换保持当前刷新率，刷新率切换保持当前分辨率，两项均采用 15 秒试用、确认保留或自动恢复。
- 目前可见的控制项取决于显示器声明的能力：亮度、对比度、RGB 增益、色彩预设和输入源。
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
