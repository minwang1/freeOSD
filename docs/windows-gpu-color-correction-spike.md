# Windows GPU 色彩校正技术验证

## 结论

可以实现一个独立于显示器 DDC/CI RGB 增益的 Windows 色彩校正功能。校正发生在 Windows/GPU 输出颜色管线，因而能在显示器固件锁定 RGB 增益的 P3 或 sRGB 模式下提供另一条调整路径。它不等同于面板硬件校准，也不保证所有应用、HDR 状态和显示驱动都以相同方式呈现。

## Windows“显示器颜色校准”使用的机制

- Windows 显示颜色校准生成显示器 ICC 配置文件。
- 传统校准数据以 `VCGT` 或 `MS00` ICC 私有标签保存为每通道 gamma ramp；Windows 校准加载器应用这些曲线。加载器的 gamma-ramp 校准管理需通过 WCS 校准管理机制启用。
- Windows 10 2004 及更新版本还支持新的 GPU 颜色转换管线。ICC 配置文件可以带 `MHC2` 标签，描述 XYZ 调整矩阵、目标 gamma 的 1D LUT 和 HDR 静态显示元数据。该管线没有供普通应用直接实时写入的 API，应用通过安装并关联包含 MHC2 数据的 ICC 配置文件来使用它。
- Windows 文档建议通过 ICC 配置文件应用颜色校准。`SetDeviceGammaRamp` 是旧式、全局影响且可被其他应用或系统重置的接口，在 HDR 下行为未定义，不适合作为产品默认实现。

## 与当前 DDC/CI 增益的区别

| 路径 | 写入位置 | 适用场景 | 主要限制 |
|---|---|---|---|
| DDC/CI VCP `0x16/0x18/0x1A` | 显示器固件中的 RGB 增益 | 显示器支持且当前图像模式允许增益控制 | P3/sRGB 等模式可能接受数值但锁定画面处理 |
| ICC `VCGT` + Windows 校准加载器 | Windows/GPU 每通道 gamma 曲线 | 传统 SDR 校准兼容路径 | 系统/驱动可能覆盖或重置；与现有校准曲线不能随意叠加；HDR 行为不可靠 |
| ICC `MHC2` + Windows 颜色管线 | Windows/GPU 矩阵与 1D LUT | 现代 SDR/HDR 管线 | 需构造有效 MHC ICC 配置文件并通过 WCS 关联；不能作为普通实时寄存器 API 使用 |

## 建议的实现阶段

1. **只读探测**：按活动显示器设备路径枚举当前默认 ICC 配置文件、用户/系统关联范围、HDR/Advanced Color 状态和新颜色管线能力。仅记录能力，不更改系统关联或 gamma ramp。
2. **只读剖析校准 ICC**：读取当前 ICC 文件的 profile header、色彩空间与设备匹配信息，并检测 `VCGT`、`MS00`、`MHC2` 标签及其尺寸/通道数据。不能把未经识别的 profile 当成可安全修改的对象。
3. **可撤销 SDR 验证**：使用单独生成的测试 ICC 文件，不覆盖用户原始 profile；保存当前显示器的精确关联和校准加载状态；安装/关联测试 profile 后读回验证；无论失败、取消或应用退出，都恢复原关联并复读确认。恢复失败必须可见并提供重试。
4. **能力分层与 UI**：仅在对应 Windows API 能力通过探测后显示“GPU 色彩校正”。与 DDC/CI 色彩预设、RGB 增益分开。说明 GPU 输出曲线影响整个桌面且可能改变校色软件/色彩管理应用结果。
5. **HDR 和多显示器验收**：先在命名 Windows 版本、GPU/驱动、SDR、多显示器矩阵上验证。HDR 只采用有效的 MHC ICC 路径；不得在 HDR 下回退调用 `SetDeviceGammaRamp`。不能验证的环境明确显示不支持。

## 需冻结的产品决策

- 第一版是否仅支持 SDR 的 VCGT profile，还是直接要求 MHC2（开发成本高且更严格依赖 Windows 颜色管线）。
- 应用提供有限的 RGB/白点调整生成自有 profile，还是先支持选择/应用已有校准 profile。不得直接改写用户现有 ICC 文件。
- “恢复”应恢复启动前关联，还是提供单独的中性 profile；推荐恢复此前的精确关联和校准管理状态。

## 依据

- Microsoft, [Windows hardware display color calibration pipeline](https://learn.microsoft.com/en-us/windows/win32/wcs/display-calibration-mhc)
- Microsoft, [SetDeviceGammaRamp](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setdevicegammaramp)
- Microsoft, [Change display brightness and color in Windows](https://support.microsoft.com/en-us/windows/hardware/display-graphics/change-display-brightness-and-color-in-windows)

## 验证范围

首版已实现 Windows 显示器 ICC profile 导入、枚举、应用和恢复：导入调用 `InstallColorProfileW` 安装后，通过活动 DisplayConfig source/adapter 路径调用 ColorProfile APIs 关联；应用前持久化原默认 profile、关联列表和作用域；应用及恢复后读回默认 profile。安装新 profile 是系统级操作，需要管理员权限。实现位于 `WindowsDisplayColorProfileService`，入口在设置页。导入流程样例位于 `docs/test-profile/test-srgb.icm`，该中性 sRGB 文件不含校准曲线，只能验证文件导入与 profile 关联流程。

当前已实现受限的只读 ICC 结构检查：读取 profile header 和 tag table，校验长度/偏移边界，并报告 profile class、色彩空间、PCS、厂商/型号字段及 `VCGT`、`MS00`、`MHC2` 标签大小。对于 VCGT，仅检查类型头、公式/采样表格式、通道数、每通道采样点数、位宽及长度一致性；对于 MHC2，检查结构签名、保留字段、LUT 点数上限、矩阵与 RGB LUT 数据块偏移/边界及 LUT 类型头。不读取两种标签的校准数值，也不代表曲线数值有效或已由 Windows 色彩管线应用。

RGB SDR 校正首版生成独立 ICC：保留源 profile 的其他标签，写入三通道 256 点 VCGT 采样表，并用私有 `mcRG` 标签保存应用内的百分比参数供重启后还原滑块显示。中性值是恒等曲线；已有表格型 VCGT 会作为底曲线组合，公式型 VCGT 或包含 MHC2 的基底会拒绝处理。应用前保存原 profile，应用后读回活动 profile；失败时立即尝试恢复之前的活动 profile。只有在 HDR 关闭、设备报告 VCGT 支持且 WCS 校准加载器开启时才允许应用。画面效果及多显示器行为仍须用户在实体设备验证。

设置页现通过 `ColorProfileGetDeviceCapabilities` 查询 `VideoCardGammaTable` 与 `MicrosoftHardwareColorV2` 能力及 MHC2 LUT/矩阵尺寸，通过 `WcsGetCalibrationManagementState` 读取系统级 VCGT 校准加载器开关，并从显示设置后端读取当前 HDR 状态。当前机器日志记录：两个能力查询 HRESULT 均为 0、VCGT/MHC2 能力均返回支持、MHC2 LUT 为 4096 项且矩阵为 3×3、WCS 校准加载器查询成功且开启、HDR 支持但关闭。当前 profile 是中性 sRGB 且没有 VCGT；这些能力状态并不证明某个校准标签已应用或画面效果可见。WCS 状态查询只读；应用不会调用需要管理员权限的 `WcsSetCalibrationManagementState`。

导入、关联、应用和恢复已通过当前 Windows 机器上的 sRGB 流程日志验证；该中性 sRGB profile 不含可见校准曲线，因此不构成画面色彩效果验收。VCGT/MHC2 能力 API 和 WCS 加载器状态已由日志确认。RGB SDR profile 生成和应用逻辑已构建通过，用户确认当前 Windows 显示器可见颜色变化；多显示器和 HDR/MHC2 实际呈现仍未验证。RGB 滑块停止后会尝试用 `SetDeviceGammaRamp` 做临时预览，点击应用才创建 ICC profile；预览在切换显示器、开启 HDR、应用持久 profile 和正常退出时尝试恢复原 ramp，并通过读回检查。该 API 可能被驱动静默忽略、系统事件重置或其他程序覆盖，HDR 下行为未定义，因此只作为 best-effort 临时预览，profile 保存仍走 VCGT 路径。设置页现提供逐项删除：只接受文件名符合 `MonitorControl-RGB-*` 且含本应用 `mcRG` 元数据的已安装文件；活动 profile 和备份记录的原始 profile 受保护。多显示器和断开显示器上的关联无法完全核验，删除行为仍待用户 Windows 实机验证。
