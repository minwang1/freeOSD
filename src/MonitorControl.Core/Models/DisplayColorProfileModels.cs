namespace MonitorControl.Core.Models;

public sealed record DisplayColorProfile(string Name, string Path, bool IsDefault);

public sealed record RgbGainAdjustment(int RedPercent, int GreenPercent, int BluePercent)
{
    public const int MinimumPercent = 50;
    public const int MaximumPercent = 150;
    public static RgbGainAdjustment Neutral { get; } = new(100, 100, 100);

    public void Validate()
    {
        if (RedPercent is < MinimumPercent or > MaximumPercent ||
            GreenPercent is < MinimumPercent or > MaximumPercent ||
            BluePercent is < MinimumPercent or > MaximumPercent)
            throw new ArgumentOutOfRangeException(nameof(RgbGainAdjustment), $"RGB gain must be between {MinimumPercent}% and {MaximumPercent}%.");
    }
}

public sealed record DisplayColorProfileState(string DisplayName, string ProfilePath, int Scope);

public sealed record IccProfileTag(string Signature, uint Offset, uint Size);

public sealed record IccVcgtSummary(
    string Encoding,
    uint ChannelCount,
    uint SamplesPerChannel,
    uint EntryBits,
    bool IsStructurallyValid,
    string? ValidationMessage);

public sealed record IccMhc2Summary(
    uint LutEntryCount,
    bool HasMatrix,
    bool HasRedLut,
    bool HasGreenLut,
    bool HasBlueLut,
    bool IsStructurallyValid,
    string? ValidationMessage);

public sealed record IccProfileInspection(
    string FilePath,
    uint DeclaredSize,
    string ProfileClass,
    string ColorSpace,
    string ConnectionSpace,
    string Manufacturer,
    string Model,
    IReadOnlyList<IccProfileTag> Tags,
    IccVcgtSummary? VcgtSummary,
    IccMhc2Summary? Mhc2Summary,
    RgbGainAdjustment? RgbGainAdjustment)
{
    public IccProfileTag? VcgtTag => Tags.FirstOrDefault(tag => tag.Signature == "vcgt");
    public IccProfileTag? Ms00Tag => Tags.FirstOrDefault(tag => tag.Signature == "MS00");
    public IccProfileTag? Mhc2Tag => Tags.FirstOrDefault(tag => tag.Signature == "MHC2");
}

public sealed record DisplayColorPipelineCapabilities(
    int Scope,
    bool VcgtQuerySucceeded,
    bool SupportsVcgt,
    int VcgtHResult,
    bool Mhc2QuerySucceeded,
    bool SupportsMhc2,
    uint RegammaLutEntryCount,
    uint CscXyzMatrixRows,
    uint CscXyzMatrixColumns,
    int Mhc2HResult,
    bool CalibrationManagementQuerySucceeded,
    bool IsCalibrationManagementEnabled,
    int CalibrationManagementWin32Error);
