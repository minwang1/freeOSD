using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace MonitorControl.App.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly LocalizationService Shared = new();
    private UiLanguage _language = UiLanguage.Chinese;

    private static readonly IReadOnlyDictionary<string, (string Zh, string En)> Texts = new Dictionary<string, (string, string)>
    {
        ["Nav.Control"] = ("◉  控制", "◉  Controls"),
        ["Nav.Profiles"] = ("▣  预设与自动化", "▣  Profiles & Automation"),
        ["Nav.Color"] = ("◈  色彩管理", "◈  Color Management"),
        ["Nav.Settings"] = ("⚙  设置", "⚙  Settings"),
        ["Header.Monitor"] = ("显示器", "Display"),
        ["Header.Rescan"] = ("⟳ 重新扫描", "⟳ Rescan"),
        ["Page.Control"] = ("控制", "Controls"),
        ["Page.ControlDescription"] = ("调节显示器画面、色彩与输入源。只显示当前设备声明支持的控制。", "Adjust display, color, and input source. Only controls supported by this device are shown."),
        ["Page.Color"] = ("色彩管理", "Color Management"),
        ["Page.Profiles"] = ("预设与自动化", "Profiles & Automation"),
        ["Settings.Title"] = ("设置", "Settings"),
        ["Settings.Appearance"] = ("外观主题", "Appearance"),
        ["Settings.AppearanceDescription"] = ("选择浅色或深色主题，也可以跟随系统外观自动切换。", "Choose a light or dark theme, or follow your system appearance."),
        ["Settings.Accent"] = ("主题色", "Accent color"),
        ["Settings.AccentDescription"] = ("柔和的强调色会自动适配浅色和深色界面。", "The accent color adapts to both light and dark themes."),
        ["Settings.Startup"] = ("启动与托盘", "Startup & tray"),
        ["Settings.LoginStartup"] = ("登录 Windows 时启动 freeOSD", "Start freeOSD when I sign in to Windows"),
        ["Settings.Language"] = ("界面语言", "Language"),
        ["Settings.LanguageDescription"] = ("选择应用界面显示语言。", "Choose the language used throughout the application."),
        ["Settings.Version"] = ("版本", "Version"),
        ["Appearance.System"] = ("跟随系统", "Follow system"),
        ["Appearance.Light"] = ("浅色主题", "Light"),
        ["Appearance.Dark"] = ("深色主题", "Dark"),
        ["Accent.Blue"] = ("浅蓝色", "Soft blue"),
        ["Accent.Green"] = ("浅绿色", "Soft green"),
        ["Accent.Orange"] = ("浅橘色", "Soft orange"),
        ["Accent.Charcoal"] = ("黑色", "Charcoal"),
        ["Language.Chinese"] = ("中文", "Chinese"),
        ["Language.English"] = ("英文", "English")
    };

    private static readonly IReadOnlyDictionary<string, string> EnglishByChinese = new Dictionary<string, string>
    {
        ["无法扫描显示器"] = "Could not scan displays", ["重试扫描"] = "Retry scan", ["尚未发现可访问的显示器"] = "No accessible displays found", ["色彩管线能力尚未读取。"] = "Color pipeline capabilities have not been read yet.",
        ["请确认显示器、显卡和连接链路支持 DDC/CI，并在显示器菜单中启用该功能。"] = "Make sure your display, graphics adapter, and connection support DDC/CI, and enable it in the display menu.",
        ["重新扫描"] = "Scan again", ["画面与色彩"] = "Image & color", ["当前显示器未声明可调节的亮度或对比度控件。"] = "The display does not report adjustable brightness or contrast controls.",
        ["应用"] = "Apply", ["切换输入源可能导致画面暂时中断。点击“应用”后会再次确认。"] = "Changing the input source may briefly interrupt the display. You will be asked to confirm after clicking Apply.",
        ["分辨率"] = "Resolution", ["目标分辨率"] = "Target resolution", ["应用分辨率（15 秒试用）"] = "Apply resolution (15-second trial)", ["系统没有报告可用分辨率。"] = "The system did not report available resolutions.",
        ["刷新率"] = "Refresh rate", ["目标刷新率"] = "Target refresh rate", ["应用刷新率（15 秒试用）"] = "Apply refresh rate (15-second trial)", ["所选分辨率没有可用刷新率选项。"] = "No refresh rates are available for the selected resolution.",
        ["重试恢复上一次设置"] = "Retry restoring previous settings", ["预设与自动化"] = "Profiles & Automation", ["保存显示器预设，并可按前台应用自动应用。"] = "Save display presets and apply them automatically based on the foreground app.",
        ["显示器预设"] = "Display presets", ["保存当前已确认的 VCP 控件值和已应用的 RGB 校准参数。应用时会逐项写入并验证；自动切换也会应用匹配预设中的 RGB 校准。"] = "Save confirmed VCP values and applied RGB calibration. Values are written and verified individually; automatic switching also applies RGB calibration from the matching preset.",
        ["预设名称"] = "Preset name", ["输入预设名称"] = "Enter a preset name", ["保存当前设置"] = "Save current settings", ["已保存预设"] = "Saved presets", ["应用预设"] = "Apply preset", ["删除"] = "Delete",
        ["应用切换"] = "App switching", ["为前台应用切换预设"] = "Switch presets for the foreground app", ["自动应用预设中的分辨率和刷新率（需先启用前台应用切换）"] = "Automatically apply resolution and refresh rate from presets (enable foreground app switching first)", ["启用后，应用规则会在切换预设时切换到保存的显示模式；每次切换仍有 15 秒自动回滚，并在读回成功后自动确认。此选项默认关闭。"] = "When enabled, app rules switch to the display mode saved in each preset. Each change has a 15-second automatic rollback and is confirmed after a successful readback. This option is off by default.", ["应用规则"] = "App rules", ["暂无应用规则"] = "No app rules", ["添加规则…"] = "Add rule…", ["删除规则"] = "Delete rule",
        ["无匹配规则时使用"] = "When no rule matches", ["不应用预设"] = "Do not apply a preset", ["添加规则时可搜索已安装程序，或使用上一个前台程序。程序规则优先匹配；未命中时应用默认预设。删除预设会同步移除关联规则，自动应用会跳过输入源设置。"] = "Search installed apps or use the previously foregrounded app when adding a rule. App rules take priority; the default preset is used if none match. Deleting a preset also removes its rules. Automatic application skips input source settings.",
        ["还没有已保存的预设。请先在上方保存预设，再创建应用规则。"] = "No presets have been saved. Save a preset above before creating app rules.", ["连接显示器后即可创建和应用预设。"] = "Connect a display to create and apply presets.", ["请先扫描并选择可访问的显示器。"] = "Scan for and select an accessible display first.",
        ["色彩管理"] = "Color Management", ["RGB 色彩调整（SDR）"] = "RGB color adjustment (SDR)", ["拖动停止后会临时预览颜色；点击应用时可命名并保存 SDR RGB ICC profile。100% 为中性，范围 50–150%。首次应用会保存原始 ICC，可随时恢复。HDR 模式暂不支持此校准方式。"] = "Color is previewed temporarily after dragging. Click Apply to name and save an SDR RGB ICC profile. 100% is neutral; range: 50–150%. The original ICC profile is saved on first apply and can be restored. HDR is not supported by this calibration method.",
        ["红色"] = "Red", ["绿色"] = "Green", ["蓝色"] = "Blue", ["应用 RGB 调整"] = "Apply RGB adjustment", ["重置为 100%"] = "Reset to 100%", ["恢复原配置"] = "Restore original profile", ["Windows ICC 色彩配置文件"] = "Windows ICC color profiles",
        ["应用已关联到当前显示器的 ICC 配置文件。首次应用会保存当前配置，之后可恢复。"] = "Apply an ICC profile associated with the selected display. The current profile is saved on first apply and can be restored later.", ["导入 ICC/ICM 文件…"] = "Import ICC/ICM file…", ["刷新列表"] = "Refresh list", ["色彩配置文件"] = "Color profile", ["默认项"] = "Default", ["应用配置文件"] = "Apply profile",
        ["安装新 profile 需要管理员权限。随附的 test-srgb.icm 只用于检查导入和关联流程，不含校准曲线，不能作为可见色彩校正效果的测试文件。"] = "Installing a profile requires administrator privileges. The bundled test-srgb.icm only checks import and association; it contains no calibration curves and cannot demonstrate visible color correction.", ["正在刷新显示器状态…"] = "Refreshing display status…",
        ["打开 freeOSD"] = "Open freeOSD", ["退出"] = "Exit", ["亮度"] = "Brightness", ["对比度"] = "Contrast", ["红色增益"] = "Red gain", ["绿色增益"] = "Green gain", ["蓝色增益"] = "Blue gain", ["色彩预设"] = "Color preset", ["输入源"] = "Input source",
        ["当前值"] = "Current value", ["就绪。"] = "Ready.", ["正在查找显示器…"] = "Searching for displays…", ["正在读取"] = "Reading", ["未连接"] = "Disconnected", ["已连接"] = "Connected", ["选择显示器以读取关联的 ICC 配置文件。"] = "Select a display to read its associated ICC profiles.",
        ["此平台暂不支持登录启动。"] = "Launch at sign-in is not supported on this platform.", ["仅为当前 Windows 用户设置，无需管理员权限。"] = "This applies to the current Windows user and does not require administrator privileges.", ["此平台暂不支持前台应用监控。"] = "Foreground app monitoring is not supported on this platform.",
        ["当前平台未提供 ICC 配置文件导入功能。"] = "ICC profile import is unavailable on this platform.", ["当前平台未提供 Windows 色彩管线能力查询。"] = "Windows color pipeline capability queries are unavailable on this platform.", ["当前 HDR 已开启；SDR VCGT RGB 校准不适用于 HDR，请关闭 HDR 后再应用。"] = "HDR is enabled. SDR VCGT RGB calibration does not apply to HDR; turn HDR off before applying.",
        ["保存对话框尚未初始化，请关闭并重新打开设置页后重试。"] = "The save dialog is not initialized. Close and reopen the settings page, then try again.", ["文件选择器尚未初始化，请关闭并重新打开设置页后重试。"] = "The file picker is not initialized. Close and reopen the settings page, then try again.",
        ["当前平台的 DDC/CI 后端尚未实现。Windows 版本目前提供显示器控制。"] = "The DDC/CI backend is not implemented for this platform. The Windows version currently provides display controls.",
        ["选择显示器 ICC/ICM 配置文件"] = "Select display ICC/ICM profile", ["ICC/ICM 配置文件"] = "ICC/ICM profile", ["为当前 RGB 调整设置 profile 文件名。应用后会安装并设为当前显示器的默认 ICC profile。"] = "Name this RGB adjustment profile. Applying installs it and sets it as the selected display's default ICC profile.",
        ["Profile 文件名"] = "Profile file name", ["取消"] = "Cancel", ["确认并应用"] = "Confirm and apply", ["保存 RGB profile"] = "Save RGB profile", ["删除 profile"] = "Delete profile", ["确认删除"] = "Confirm deletion",
        ["搜索已安装程序"] = "Search installed apps", ["使用上一个前台程序"] = "Use previously foregrounded app", ["未选择程序"] = "No app selected", ["添加规则"] = "Add rule", ["添加应用规则"] = "Add app rule",
        ["还没有记录到上一个前台程序。先切换到目标程序，再返回此窗口添加规则。"] = "No previously foregrounded app was recorded. Switch to the target app, then return here to add the rule.",
        ["没有找到可执行文件路径有效的已安装程序。也可以使用“上一个前台程序”。"] = "No installed apps with valid executable paths were found. You can also use the previously foregrounded app.",
        ["切换输入源？"] = "Change input source?", ["切换输入源"] = "Change input source", ["立即恢复"] = "Revert now", ["保留此设置"] = "Keep these settings", ["确认显示设置"] = "Confirm display settings",
        ["时间已到，正在恢复原显示设置…"] = "Time is up. Restoring the original display settings…", ["未知错误"] = "Unknown error",
        ["此 Windows 版本或当前显示器不支持 ICC profile 管理。"] = "ICC profile management is unsupported by this Windows version or display.", ["此显示器没有可选择的已关联 ICC 配置文件。"] = "No associated ICC profiles are available for this display.",
        ["选择配置文件后，可查看 ICC 文件头和校准标签摘要。"] = "Select a profile to inspect its ICC header and calibration tag summary.", ["正在只读检查 ICC 文件…"] = "Inspecting ICC file (read-only)…",
        ["当前 profile 不是由本应用生成，无法从 ICC 通用数据还原其 RGB 参数。"] = "The active profile was not generated by this app, so its RGB values cannot be recovered from generic ICC data.",
        ["请先扫描并选择一个显示器，再导入 ICC 配置文件。"] = "Scan for and select a display before importing an ICC profile.", ["正在安装并关联 ICC 配置文件…"] = "Installing and associating ICC profile…", ["正在恢复原 ICC 配置文件…"] = "Restoring original ICC profile…", ["已恢复并验证原 ICC 配置文件。"] = "Restored and verified the original ICC profile.",
        ["当前活动配置的 RGB 参数未知；应用后会保存本应用的参数。"] = "RGB values of the active profile are unknown; applying will save this app's values.", ["参数有更改，尚未应用。"] = "Values have changed and are not applied yet.", ["参数与当前应用的 RGB 配置一致。"] = "Values match the active RGB configuration.",
        ["恢复显示设置失败。"] = "Failed to restore display settings.", ["HDR 已开启。"] = "HDR is on.", ["HDR 已关闭。"] = "HDR is off.", ["当前显示器不支持 HDR。"] = "The current display does not support HDR.",
        ["当前显示器或连接路径不支持 HDR 控制。"] = "HDR control is unsupported by the current display or connection path.", ["当前显示器或平台不支持查询显示模式。"] = "The current display or platform cannot query display modes.", ["当前无法查询显示模式。"] = "Display modes cannot be queried right now.",
        ["选择分辨率后显示该分辨率支持的刷新率。选择后可先试用 15 秒。"] = "Select a resolution to see its supported refresh rates. You can trial a selection for 15 seconds.", ["当前平台可读取刷新率，但不支持修改。"] = "This platform can read refresh rates but cannot change them.", ["系统支持的分辨率按从高到低排列。"] = "Supported resolutions are listed from highest to lowest.", ["当前平台不支持修改分辨率。"] = "Changing resolution is not supported on this platform.",
        ["仅列出所选分辨率下支持的刷新率，按从高到低排列。"] = "Only refresh rates supported by the selected resolution are listed, from highest to lowest.", ["系统未报告该分辨率在当前刷新率下可用；请先选择一个该分辨率支持的刷新率。"] = "The system did not report this resolution at the selected refresh rate. Choose a refresh rate supported by the resolution first.",
        ["分辨率试用失败；若模式已更改，超时回滚仍会继续。"] = "The resolution trial failed. Timed rollback will continue if the mode changed.", ["刷新率试用失败；若模式已更改，超时回滚仍会继续。"] = "The refresh-rate trial failed. Timed rollback will continue if the mode changed.", ["恢复仍然失败；可再次重试。"] = "Restoration still failed; you can retry again.",
        ["恢复原分辨率失败；可重试恢复。"] = "Failed to restore the previous resolution; you can retry.", ["恢复原刷新率失败；可重试恢复。"] = "Failed to restore the previous refresh rate; you can retry.", ["重试恢复失败。"] = "Retrying restoration failed.",
        ["已取消输入源切换。"] = "Input source change cancelled.", ["没有可保存的已确认控件值或当前活动 ICC/RGB 设置。"] = "There are no confirmed control values or active ICC/RGB settings to save.",
        ["无法确定显示器名称。"] = "Could not determine the display name.", ["当前平台不支持 ICC 配置文件应用。"] = "Applying ICC profiles is not supported on this platform.", ["当前平台不支持 RGB ICC 校准。"] = "RGB ICC calibration is not supported on this platform.",
        ["当前显示器或 Windows 校准加载器不支持 VCGT，已跳过 RGB 校准。"] = "VCGT is unsupported by this display or the Windows calibration loader; RGB calibration was skipped.", ["当前 HDR 已开启，已跳过 SDR RGB 校准。"] = "HDR is enabled; SDR RGB calibration was skipped.",
        ["前台应用自动切换已启用；优先使用应用规则，未命中时应用默认预设。"] = "Automatic foreground-app switching is enabled. App rules take priority; the default preset is used when none match.", ["前台应用自动切换已关闭。"] = "Automatic foreground-app switching is disabled.",
    };

    private static readonly (string Zh, string En)[] Fragments =
    [
        ("正在写入 ", "Writing "), ("写入错误：", "Write error: "), ("；读回错误：", "; readback error: "), ("；显示器返回 ", "; display returned "),
        ("写入 ", "Write "), (" 失败：", " failed: "), ("读取预设失败：", "Failed to load presets: "), ("保存预设失败：", "Failed to save preset: "), ("删除预设失败：", "Failed to delete preset: "),
        ("已加载 ", "Loaded "), (" 个预设。", " preset(s)."), ("已保存预设“", "Saved preset ‘"), ("”，包含 ", "’, containing "), (" 项 VCP 控件", " VCP control(s)"), ("及 RGB（", " and RGB ("), ("及 ICC（", " and ICC ("), ("及显示模式（", " and display mode ("),
        ("已删除预设“", "Deleted preset ‘"), ("VCP 0x", "VCP 0x"), ("当前显示器没有该控件。", " is not available on the current display."), ("读回值为 ", "Readback value: "), ("，目标值为 ", ", target value: "),
        ("ICC 配置文件属于另一台显示器，已跳过以避免误应用。", "Skipped the ICC profile because it belongs to another display."), ("当前平台不支持 ICC 配置文件应用。", "Applying ICC profiles is not supported on this platform."), ("无法确定显示器名称。", "Could not determine the display name."),
        ("当前平台不支持 RGB ICC 校准。", "RGB ICC calibration is not supported on this platform."), ("当前显示器或 Windows 校准加载器不支持 VCGT，已跳过 RGB 校准。", "VCGT is unsupported by this display or the Windows calibration loader; RGB calibration was skipped."), ("RGB 校准：", "RGB calibration: "),
        ("显示模式预设属于另一台显示器，已跳过以避免误应用。", "Skipped the display mode because it belongs to another display."), ("当前平台不支持自动设置分辨率和刷新率。", "Automatic resolution and refresh-rate changes are not supported on this platform."), ("当前显示器不支持 ", "The current display does not support "), ("模式切换后读回不匹配，已恢复原模式。", "The mode readback did not match after switching; the original mode was restored."), ("已取消显示模式更改，恢复原模式。", "Display mode change cancelled; the original mode was restored."), ("显示模式：", "Display mode: "), ("已有显示模式试用正在进行，请完成或等待回滚后再应用预设。", "A display-mode trial is already active. Finish it or wait for rollback before applying a preset."), ("原显示模式尚未恢复，请先在控制页重试恢复。", "The previous display mode has not been restored. Retry restoration on the Controls page first."),
        ("HDR 已开启。", "HDR is on."), ("HDR 已关闭。", "HDR is off."), ("HDR 设置失败：", "Failed to update HDR: "), ("正在开启 HDR…", "Enabling HDR…"), ("正在关闭 HDR…", "Disabling HDR…"),
        ("已切换到 ", "Switched to "), ("正在切换到 ", "Switching to "), ("正在读取 ", "Reading "), ("刷新 ", "Refreshing "), (" 的显示器能力。", " display capabilities."),
        ("扫描显示器失败：", "Display scan failed: "), ("读取显示器 ICC 配置文件失败：", "Failed to read display ICC profiles: "), ("读取色彩管线能力失败：", "Failed to read color pipeline capabilities: "),
        ("正在应用“", "Applying ‘"), ("”失败：", "’ failed: "), ("应用 ICC 配置文件失败：", "Failed to apply ICC profile: "), ("导入 ICC 配置文件失败：", "Failed to import ICC profile: "), ("恢复 ICC 配置文件失败，可重试：", "Failed to restore ICC profile; retry: "),
        ("已应用 RGB 校准：R ", "Applied RGB calibration: R "), ("RGB 校准应用失败：", "Failed to apply RGB calibration: "), ("临时预览：R ", "Temporary preview: R "), ("RGB 临时预览不可用：", "RGB preview unavailable: "), ("恢复临时 RGB 预览失败：", "Failed to restore temporary RGB preview: "),
        ("正在读取可用刷新率…", "Reading available refresh rates…"), ("读取刷新率失败。", "Failed to read refresh rates."), ("读取分辨率失败。", "Failed to read resolutions."), ("请先选择显示器。", "Select a display first."), ("已取消输入源切换。", "Input source change cancelled."),
        ("此平台暂不支持", "Not supported on this platform"), ("读取登录启动状态失败：", "Failed to read launch-at-sign-in status: "), ("更新登录启动设置失败：", "Failed to update launch-at-sign-in setting: "),
        ("前台应用自动切换已关闭。", "Automatic foreground-app switching is disabled."), ("自动化预设“", "Automation preset ‘"), ("”不存在。", "’ does not exist."), ("检测到 ", "Detected "), ("，正在应用“", ", applying ‘"),
        ("正在生成并应用 SDR RGB 校准 profile…", "Generating and applying SDR RGB calibration profile…"), ("正在安装并关联 ICC 配置文件…", "Installing and associating ICC profile…"), ("正在恢复原 ICC 配置文件…", "Restoring original ICC profile…"), ("已恢复并验证原 ICC 配置文件。", "Restored and verified the original ICC profile."),
        ("确定删除 RGB profile“", "Delete RGB profile ‘"), ("”吗？", "’?"), ("选择要应用预设“", "Select installed apps to use preset ‘"), ("”的已安装程序。", "’."),
        ("将显示器输入切换为“", "Change display input to ‘"), ("”。画面可能暂时中断。是否继续？", "’. The display may be interrupted briefly. Continue?"),
        ("显示模式已临时切换到 ", "The display mode was temporarily changed to "), ("、", " and "), ("。\n如果画面显示正常，请在倒计时结束前保留；否则选择恢复或等待自动恢复。", ".\nKeep these settings before the countdown ends if the display looks correct; otherwise revert or wait for automatic restoration."),
        ("剩余 ", "Time remaining: "), (" 秒", " seconds"), ("自动恢复失败：", "Automatic restoration failed: "), ("。请返回主界面重试恢复。", ". Return to the main window and retry restoration."),
        ("当前值 ", "Current value "), ("（未映射）", " (unmapped)"), ("当前活动 profile 已应用 RGB：R ", "RGB values applied by the active profile: R "), ("当前 profile 不是由本应用生成，无法从 ICC 通用数据还原其 RGB 参数。", "The active profile was not generated by this app, so its RGB values cannot be recovered from generic ICC data."),
        ("当前 profile ", "Active profile "), ("查询失败 (", "Query failed ("), ("支持（LUT ", "Supported (LUT "), (" 项，矩阵 ", " entries, matrix "), ("×", " × "), ("已开启", "On"), ("已关闭", "Off"), ("未知", "Unknown"), ("不支持或无法读取", "Unsupported or unavailable"),
        ("只读能力：VCGT ", "Read-only capabilities: VCGT "), ("；系统校准加载器 ", "; system calibration loader "), ("；MHC2 ", "; MHC2 "), ("；HDR ", "; HDR "), ("。能力支持不代表所选 ICC 含有效校准数据或画面已发生变化。", ". Capability support does not mean the ICC contains valid calibration data or that the image changed."),
        ("已读取 ", "Loaded "), (" 个配置文件。当前：", " profile(s). Active: "), ("只读检查失败：", "Read-only inspection failed: "), ("读取显示器 ICC 配置文件失败：", "Failed to read display ICC profiles: "), ("应用 ICC 配置文件失败：", "Failed to apply ICC profile: "), ("导入 ICC 配置文件失败：", "Failed to import ICC profile: "),
        ("已应用“", "Applied ‘"), ("”。画面效果取决于 Windows 颜色管线和应用支持。", "’. The visible result depends on the Windows color pipeline and application support."), ("正在预览 RGB 调整（临时 Gamma Ramp）…", "Previewing RGB adjustment (temporary gamma ramp)…"),
        ("临时预览：R ", "Temporary preview: R "), ("%。点击“应用 RGB 调整”可保存为 ICC profile。", "%. Click Apply RGB Adjustment to save an ICC profile."), ("RGB 临时预览不可用：", "RGB preview unavailable: "),
        ("恢复临时 RGB 预览失败：", "Failed to restore temporary RGB preview: "), ("当前 HDR 已开启；SDR VCGT RGB 校准不适用于 HDR，请关闭 HDR 后再应用。", "HDR is enabled. SDR VCGT RGB calibration does not apply to HDR; turn HDR off before applying."),
        ("已应用 RGB 校准：R ", "Applied RGB calibration: R "), ("。Windows 已读回活动 profile；画面效果需实机确认。", ". Windows read back the active profile; verify the visual result on the display."), ("RGB 校准应用失败：", "Failed to apply RGB calibration: "),
        ("已导入并关联“", "Imported and associated ‘"), ("”。它尚未设为默认；如需应用，请点击“应用配置文件”。", "’. It is not the default profile. Click Apply Profile to use it."), ("正在删除 RGB profile“", "Deleting RGB profile ‘"), ("已删除 RGB profile“", "Deleted RGB profile ‘"), ("”。”", "’."), ("删除 RGB profile 失败：", "Failed to delete RGB profile: "),
        ("更新登录启动设置失败：", "Failed to update launch-at-sign-in setting: "), ("读取登录启动状态失败：", "Failed to read launch-at-sign-in status: "), ("已启用登录时启动。", "Launch at sign-in enabled."), ("已关闭登录时启动。", "Launch at sign-in disabled."),
        ("已将 ", "Bound "), (" 绑定到预设“", " to preset ‘"), (" 条应用规则。自动切换", " app rule(s). Automatic switching "), ("已加载 ", "Loaded "), ("前台应用自动切换已启用；优先使用应用规则，未命中时应用默认预设。", "Automatic foreground-app switching is enabled. App rules take priority; the default preset is used when none match."), ("自动化预设“", "Automation preset ‘"), ("”不存在。", "’ does not exist."), ("检测到 ", "Detected "), ("，正在应用“", ", applying ‘"),
        ("刷新失败，仍显示上次读取的数据：", "Refresh failed; showing the last loaded data: "), ("扫描显示器失败：", "Display scan failed: "), ("刷新 ", "Refreshing "), (" 失败，仍显示上次读取的数据：", " failed; showing the last loaded data: "), (" 的显示器能力。", " display capabilities."), ("正在读取可用刷新率…", "Reading available refresh rates…"),
        ("读取自动化规则失败：", "Failed to load automation rules: "), ("保存自动化规则失败：", "Failed to save automation rules: "), ("无法监听前台程序：", "Could not monitor foreground app: "), ("已删除应用规则“", "Deleted app rule ‘"),
        ("已应用预设“", "Applied preset ‘"), ("”，共 ", "’, "), (" 项。", " item(s)."), ("预设部分应用：成功 ", "Preset partially applied: "), (" 项，失败 ", " succeeded, "), (" 项。\n", " failed.\n"), ("已启用", "enabled"), ("已关闭", "disabled"),
        ("此显示器没有可选择的已关联 ICC 配置文件。", "No associated ICC profiles are available for this display."), (" 个配置文件。当前：", " profile(s). Active: "), ("查询失败 (", "Query failed ("), ("支持（LUT ", "Supported (LUT "), (" 项，矩阵 ", " entries, matrix "), ("未知", "Unknown"), ("不支持或无法读取", "Unsupported or unavailable"), ("不支持", "Unsupported"), ("支持", "Supported"),
        ("有（", "Present ("), (" 字节）", " bytes)"), ("无", "None"), ("结构异常：", "Invalid structure: "), ("公式型，", "Formula, "), (" 通道", " channel(s)"), ("采样表，", "Sampled table, "), ("点/通道", "point(s)/channel"), ("位", "bit(s)"), ("点，矩阵 ", "point(s), matrix "), ("，RGB LUT ", ", RGB LUT "), ("VCGT：", "VCGT: "), ("；结构：", "; structure: "), ("MS00：", "MS00: "), ("MHC2：", "MHC2: "), (" · ", " · "), (" → ", " → "),
        ("当前活动 profile 的 RGB 校准状态尚未读取。", "The active profile's RGB calibration status has not been read yet."), ("当前活动 profile 已应用 RGB：R ", "RGB values applied by the active profile: R "), ("当前 profile 不是由本应用生成，无法从 ICC 通用数据还原其 RGB 参数。", "The active profile was not generated by this app, so its RGB values cannot be recovered from generic ICC data."),
        ("此 Windows 版本或当前显示器不支持 ICC profile 管理。", "ICC profile management is unsupported by this Windows version or the current display."), ("当前平台未提供 Windows 色彩管线能力查询。", "Windows color pipeline capability queries are unavailable on this platform."), ("当前 HDR 已开启；SDR VCGT RGB 校准不适用于 HDR，请关闭 HDR 后再应用。", "HDR is enabled. SDR VCGT RGB calibration does not apply to HDR; turn HDR off before applying."),
        ("只读能力：", "Read-only capabilities: "), ("VCGT ", "VCGT "), ("；系统校准加载器 ", "; system calibration loader "), ("；MHC2 ", "; MHC2 "), ("；HDR ", "; HDR "), ("。能力支持不代表所选 ICC 含有效校准数据或画面已发生变化。", ". Capability support does not mean the ICC contains valid calibration data or that the image changed."),
        ("当前显示器或 Windows 校准加载器不支持 VCGT，已跳过 RGB 校准。", "VCGT is unsupported by this display or the Windows calibration loader; RGB calibration was skipped."), ("当前平台不支持 ICC 配置文件应用。", "Applying ICC profiles is not supported on this platform."), ("当前平台不支持 RGB ICC 校准。", "RGB ICC calibration is not supported on this platform."),
        ("系统未报告该分辨率在当前刷新率下可用；请先选择一个该分辨率支持的刷新率。", "The system did not report this resolution at the selected refresh rate. Choose a supported refresh rate first."), ("当前平台不支持修改分辨率。", "Changing resolution is not supported on this platform."), ("当前平台可读取刷新率，但不支持修改。", "This platform can read refresh rates but cannot change them."),
        ("当前显示器不支持 HDR。", "The current display does not support HDR."), ("当前显示器或连接路径不支持 HDR 控制。", "HDR control is unsupported by the current display or connection path."), ("读取刷新率失败。", "Failed to read refresh rates."), ("读取分辨率失败。", "Failed to read resolutions."),
        ("前台应用自动切换已启用；优先使用应用规则，未命中时应用默认预设。", "Automatic foreground-app switching is enabled. App rules take priority; the default preset is used when none match."), ("自动化预设“", "Automation preset ‘"), ("”不存在。", "’ does not exist."), ("检测到 ", "Detected "), ("，正在应用“", ", applying ‘"),
        ("ICC 文件只读检查失败：", "ICC read-only inspection failed: "), ("读取色彩管线能力失败：", "Failed to read color pipeline capabilities: "), ("文件选择器尚未初始化，请关闭并重新打开设置页后重试。", "The file picker is not initialized. Close and reopen the settings page, then try again."),
        ("读取显示器 ICC 配置文件失败：", "Failed to read display ICC profiles: "), ("读取 ICC 配置文件失败：", "Failed to read ICC profile: "), ("当前平台未提供 ICC 配置文件导入功能。", "ICC profile import is unavailable on this platform."), ("请先扫描并选择一个显示器，再导入 ICC 配置文件。", "Scan for and select a display before importing an ICC profile."),
        ("ICC 文件只读检查失败：", "ICC read-only inspection failed: "), ("正在应用“", "Applying ‘"), ("已应用“", "Applied ‘"), ("”。”", "’."), ("画面效果取决于 Windows 颜色管线和应用支持。", "The visible result depends on the Windows color pipeline and application support."), ("已导入并关联“", "Imported and associated ‘"), ("”。它尚未设为默认；如需应用，请点击“应用配置文件”。", "’. It is not the default profile. Click Apply Profile to use it."),
        ("亮度", "Brightness"), ("对比度", "Contrast"), ("红色增益", "Red gain"), ("绿色增益", "Green gain"), ("蓝色增益", "Blue gain"), ("色彩预设", "Color preset"), ("输入源", "Input source"), ("用户 1", "User 1"), ("用户 2", "User 2"),
        ("MHC2 结构头不足 36 字节。", "MHC2 structure header is shorter than 36 bytes."), ("MHC2 类型签名不匹配。", "MHC2 type signature does not match."), ("MHC2 保留字段非零。", "MHC2 reserved field is nonzero."), ("LUT 点数 ", "LUT entry count "), (" 超过 Windows 上限 4096。", " exceeds the Windows limit of 4096."),
        ("矩阵数据超出 MHC2 标签边界。", "Matrix data exceeds the MHC2 tag boundary."), ("LUT 数据头超出 MHC2 标签边界。", "LUT data header exceeds the MHC2 tag boundary."), ("LUT 数据类型不是 sf32。", "LUT data type is not sf32."), ("LUT 保留字段非零。", "LUT reserved field is nonzero."), ("LUT 数据超出 MHC2 标签边界。", "LUT data exceeds the MHC2 tag boundary."),
        ("标签头不足 12 字节。", "Tag header is shorter than 12 bytes."), ("标签签名不匹配。", "Tag signature does not match."), ("公式类型数据不足 48 字节。", "Formula type data is shorter than 48 bytes."), ("不支持的 gamma 类型 ", "Unsupported gamma type "), ("表格类型头不足 18 字节。", "Table type header is shorter than 18 bytes."),
        ("通道数和每通道采样点数必须大于零。", "Channel count and sample count per channel must be greater than zero."), ("每个采样值的字节数 ", "Bytes per sample "), (" 不受支持。", " are not supported."), ("采样表长度不足（需要 ", "Sample table is too short (requires "), (" 字节，标签为 ", " bytes; tag has "), (" 字节）。", " bytes).")
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
    public static LocalizationService Instance => Shared;
    public UiLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    public static string Translate(string source)
    {
        if (Instance.Language == UiLanguage.Chinese) return Texts.TryGetValue(source, out var zh) ? zh.Zh : source;
        if (Texts.TryGetValue(source, out var pair)) return pair.En;
        foreach (var text in Texts.Values)
            if (text.Zh == source) return text.En;
        if (EnglishByChinese.TryGetValue(source, out var translation)) return translation;
        var result = source;
        foreach (var (zh, en) in Fragments.OrderByDescending(item => item.Zh.Length))
            result = result.Replace(zh, en, StringComparison.Ordinal);
        return result;
    }

    public static void SetLanguage(UiLanguage language) => Instance.Language = language;
}

public sealed class LocExtension(string source) : MarkupExtension
{
    public override object ProvideValue(System.IServiceProvider serviceProvider) => CompiledBinding.Create(
        static (LocalizationService localizationService) => localizationService.Language,
        LocalizationService.Instance,
        new LocalizedStringConverter(source),
        BindingMode.OneWay);
}

internal sealed class LocalizedStringConverter(string source) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => LocalizationService.Translate(source);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => BindingOperations.DoNothing;
}
