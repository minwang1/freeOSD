# 显示器控制（Windows DDC/CI）

基于 Python、PySide6 和 Windows DXVA2 API 的桌面显示器控制程序。需要在 Windows 10/11 原生环境运行；不支持 WSL。显示器、显卡、扩展坞及连接线都需要支持 DDC/CI，并且通常需要在显示器菜单中启用该功能。

## 功能

- 自动查找 Windows 可访问的实体显示器，并读取其 DDC/CI 能力。
- 窗口从后台重新激活时重新扫描并读取显示器与 Windows 设置，更新刷新率、亮度、对比度、RGB、色彩预设、输入源和自定义控制面板当前值；重新扫描也会重新获取可能因显示模式切换而失效的监视器句柄。
- 按显示器支持情况调节亮度、对比度、RGB 增益、色彩预设和输入源。输入源切换前会提示确认。
- “Windows display settings” 区域可查看所选显示器当前分辨率下可用的刷新率，并切换刷新率；切换后需在 15 秒内确认，否则自动恢复原刷新率。可读取并切换 Windows HDR 状态。此处使用 Windows 显示配置 API，与显示器 DDC/CI 控件分开；HDR 和模式支持取决于 Windows 版本、显卡驱动及连接链路。
- “Identify monitor settings” 提供亮度、对比度、色彩空间、画面模式、FreeSync/Adaptive-Sync、HDR、OD、局部调光、色温、锐度、动态对比度和输入源等常见 OSD 选项及状态默认值；两个下拉框都允许输入自定义名称和值。用户可为同一选项连续采集多个状态。
- 每次采集会记录带 OSD 标签的完整 VCP 快照，并自动比较同一选项下相邻状态的变化；界面会显示变化代码及前后值。识别报告附带 OSD 状态到 VCP 值、VCP 值到 OSD 状态的关联汇总，也保留变化项、未变化项、读取失败项，以及供 ChatGPT 分析的方法和限制。导入报告后，可在分析结果中逐项测试 OSD/VCP 映射：对每个候选值写入后等待 4 秒并读回，再写回测试前的值、等待 4 秒并读回；两次读回都与目标值一致才显示通过。每个候选项都会单独恢复初始值。测试结果会显示在该映射旁；用户可再勾选通过的映射，添加到主界面的“Identified control panels”。主面板可选择已记录的原始 VCP 值，按“Apply”后立即写入，也可删除面板。测试期间显示器设置会暂时变化。面板配置保存在当前用户的应用数据目录 `identified-controls.json`。程序会额外探测厂商代码 0xC0–0xFF；若显示器未返回能力字符串，还会探测常见控制代码，并标明未声明的探测项。报告由用户选择保存位置。
- 将当前显示器可读取的所有已声明 VCP 设置、已添加的自定义控制值，以及 Windows 刷新率和 HDR 状态保存为命名预设；支持应用和删除预设。无法读取的 VCP 或 Windows 设置会在保存时列出。预设以 JSON 保存在当前用户的应用数据目录。应用预设会明确提示可能造成的显示中断。
- 为多个已安装桌面程序分别绑定预设。程序从可搜索的已安装应用列表中选择；绑定程序失去焦点、且没有其他应用规则匹配时，可恢复指定的默认预设。自动切换不会更改输入源。
- 支持系统托盘（含打开窗口和退出）、关闭窗口时最小化到托盘、可选的 Windows 登录启动，以及窗口位置和大小保存。
- 显示器扫描与 DDC/CI 读写在工作线程中串行执行，避免慢速显示器操作阻塞界面。JSON 日志会轮换保存到当前用户的应用数据目录。
- Windows 11 风格的分区界面；原始显示器能力信息可按需展开。

## 环境要求

- Windows 10 或 Windows 11
- 原生 Windows Python 3.12 或更新版本
- 支持 DDC/CI 的显示器和连接路径

## 安装依赖并运行源码

在项目根目录打开 PowerShell：

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
python .\src\main.py
```

运行硬件无关的单元测试：

```powershell
python -m unittest discover -s tests -v
```

## 编译 Windows 程序包

在项目根目录的 PowerShell 中安装运行与打包依赖，并执行打包脚本：

```powershell
py -3.12 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -r requirements-build.txt
.\build_windows.ps1
```

打包产物位于 `dist\MonitorControl\MonitorControl.exe`。这是 onedir 目录包；分发和运行时需要保留整个 `dist\MonitorControl` 文件夹。

如果 PowerShell 因执行策略拒绝运行脚本，可仅对当前窗口临时放行后重试：

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\build_windows.ps1
```

## 使用提示与限制

- 设置会直接写入实体显示器。多数操作立即生效；输入源切换可能导致画面暂时中断。
- 预设只记录当前显示器能读取的设置；自动切换面向可识别的传统桌面程序。未出现在列表中的桌面程序，可先运行程序，再使用“Bind last app”绑定。部分 UWP 应用共用系统宿主进程，无法单独识别。
- 不同显示器对 DDC/CI 的支持不一致。能力列表宣称支持某项功能，也不保证显示器能正确读写该功能。
- FreeSync、HDR、OD 等设置可能不通过 DDC/CI 暴露；识别报告采集显示器声明支持的 VCP 项，未发现变化不能证明该设置没有作用。分析时请同时提供 OSD 值备注，并将厂商专用映射标记为推测。
- 本项目是原型，不是经过认证的 MCCS 实现。
