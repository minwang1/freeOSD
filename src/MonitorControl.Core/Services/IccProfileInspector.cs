using System.Buffers.Binary;
using System.Text;
using MonitorControl.Core.Models;

namespace MonitorControl.Core.Services;

/// <summary>Reads ICC metadata and tag boundaries without changing the profile.</summary>
public static class IccProfileInspector
{
    private const int HeaderSize = 128;
    private const int TagTableHeaderSize = 4;
    private const int TagEntrySize = 12;
    private const uint MaximumProfileBytes = 64 * 1024 * 1024;
    private const uint MaximumTagCount = 4096;

    public static IccProfileInspection Inspect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length < HeaderSize + TagTableHeaderSize)
            throw new InvalidDataException("The ICC file is too short to contain a profile header and tag table.");
        if (stream.Length > MaximumProfileBytes)
            throw new InvalidDataException($"The ICC file exceeds the {MaximumProfileBytes / (1024 * 1024)} MB inspection limit.");

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (!header.Slice(36, 4).SequenceEqual("acsp"u8))
            throw new InvalidDataException("The selected file does not have a valid ICC profile signature.");

        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (declaredSize < HeaderSize + TagTableHeaderSize || declaredSize > stream.Length)
            throw new InvalidDataException("The ICC profile header declares an invalid file size.");

        Span<byte> countBytes = stackalloc byte[TagTableHeaderSize];
        stream.Position = HeaderSize;
        stream.ReadExactly(countBytes);
        var tagCount = BinaryPrimitives.ReadUInt32BigEndian(countBytes);
        if (tagCount > MaximumTagCount)
            throw new InvalidDataException($"The ICC profile tag count ({tagCount}) exceeds the safe inspection limit.");

        var tableEnd = (ulong)HeaderSize + TagTableHeaderSize + ((ulong)tagCount * TagEntrySize);
        if (tableEnd > declaredSize)
            throw new InvalidDataException("The ICC profile tag table extends beyond the declared profile size.");

        var tags = new List<IccProfileTag>(checked((int)tagCount));
        Span<byte> entry = stackalloc byte[TagEntrySize];
        for (var index = 0; index < tagCount; index++)
        {
            stream.ReadExactly(entry);
            var signature = ReadSignature(entry[..4]);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..8]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..12]);
            var tagEnd = (ulong)offset + size;
            if (offset < tableEnd || tagEnd > declaredSize)
                throw new InvalidDataException($"ICC tag '{signature}' points outside the declared profile data.");
            tags.Add(new IccProfileTag(signature, offset, size));
        }

        var vcgtTag = tags.FirstOrDefault(tag => tag.Signature == "vcgt");
        var vcgtSummary = vcgtTag is null ? null : ReadVcgtSummary(stream, vcgtTag);
        var mhc2Tag = tags.FirstOrDefault(tag => tag.Signature == "MHC2");
        var mhc2Summary = mhc2Tag is null ? null : ReadMhc2Summary(stream, mhc2Tag);
        var rgbGainAdjustment = ReadRgbGainAdjustment(stream, tags.FirstOrDefault(tag => tag.Signature == "mcRG"));

        return new IccProfileInspection(
            Path.GetFullPath(filePath),
            declaredSize,
            DescribeSignature(ReadSignature(header[12..16]), "ICC profile class"),
            DescribeSignature(ReadSignature(header[16..20]), "color space"),
            DescribeSignature(ReadSignature(header[20..24]), "connection space"),
            DescribeOptionalSignature(ReadSignature(header[48..52])),
            DescribeOptionalSignature(ReadSignature(header[52..56])),
            tags,
            vcgtSummary,
            mhc2Summary,
            rgbGainAdjustment);
    }

    private static RgbGainAdjustment? ReadRgbGainAdjustment(Stream stream, IccProfileTag? tag)
    {
        if (tag is null || tag.Size != 16) return null;
        Span<byte> data = stackalloc byte[16];
        stream.Position = tag.Offset;
        stream.ReadExactly(data);
        if (!data[..4].SequenceEqual("mcRG"u8) || BinaryPrimitives.ReadUInt32BigEndian(data[4..8]) != 1 ||
            BinaryPrimitives.ReadUInt16BigEndian(data[14..16]) != 0)
            return null;
        var adjustment = new RgbGainAdjustment(
            BinaryPrimitives.ReadUInt16BigEndian(data[8..10]),
            BinaryPrimitives.ReadUInt16BigEndian(data[10..12]),
            BinaryPrimitives.ReadUInt16BigEndian(data[12..14]));
        try
        {
            adjustment.Validate();
            return adjustment;
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static IccMhc2Summary ReadMhc2Summary(Stream stream, IccProfileTag tag)
    {
        static IccMhc2Summary Invalid(uint entries, bool matrix, bool red, bool green, bool blue, string message) =>
            new(entries, matrix, red, green, blue, false, message);
        if (tag.Size < 36)
            return Invalid(0, false, false, false, false, "MHC2 结构头不足 36 字节。");

        Span<byte> header = stackalloc byte[36];
        stream.Position = tag.Offset;
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("MHC2"u8))
            return Invalid(0, false, false, false, false, "MHC2 类型签名不匹配。");
        if (BinaryPrimitives.ReadUInt32BigEndian(header[4..8]) != 0)
            return Invalid(0, false, false, false, false, "MHC2 保留字段非零。");

        var lutEntries = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        var matrixOffset = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        var redOffset = BinaryPrimitives.ReadUInt32BigEndian(header[24..28]);
        var greenOffset = BinaryPrimitives.ReadUInt32BigEndian(header[28..32]);
        var blueOffset = BinaryPrimitives.ReadUInt32BigEndian(header[32..36]);
        var hasMatrix = matrixOffset != 0;
        var hasRed = redOffset != 0;
        var hasGreen = greenOffset != 0;
        var hasBlue = blueOffset != 0;
        if (lutEntries > 4096)
            return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, $"LUT 点数 {lutEntries} 超过 Windows 上限 4096。");

        if (hasMatrix && (ulong)matrixOffset + 48 > tag.Size)
            return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, "矩阵数据超出 MHC2 标签边界。");

        var lutOffsets = new[] { redOffset, greenOffset, blueOffset };
        var lutBytes = lutEntries == 0 ? 8UL : 8UL + (ulong)lutEntries * 4;
        Span<byte> lutHeader = stackalloc byte[8];
        for (var index = 0; index < lutOffsets.Length; index++)
        {
            var offset = lutOffsets[index];
            if (offset == 0) continue;
            if ((ulong)offset + 8 > tag.Size)
                return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, "LUT 数据头超出 MHC2 标签边界。");
            stream.Position = (long)tag.Offset + offset;
            stream.ReadExactly(lutHeader);
            if (!lutHeader[..4].SequenceEqual("sf32"u8))
                return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, "LUT 数据类型不是 sf32。");
            if (BinaryPrimitives.ReadUInt32BigEndian(lutHeader[4..8]) != 0)
                return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, "LUT 保留字段非零。");
            if ((ulong)offset + lutBytes > tag.Size)
                return Invalid(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, "LUT 数据超出 MHC2 标签边界。");
        }

        return new IccMhc2Summary(lutEntries, hasMatrix, hasRed, hasGreen, hasBlue, true, null);
    }

    private static IccVcgtSummary ReadVcgtSummary(Stream stream, IccProfileTag tag)
    {
        static IccVcgtSummary Invalid(string message) => new("Unknown", 0, 0, 0, false, message);
        if (tag.Size < 12)
            return Invalid("标签头不足 12 字节。");

        Span<byte> header = stackalloc byte[18];
        stream.Position = tag.Offset;
        stream.ReadExactly(header[..12]);
        if (!header[..4].SequenceEqual("vcgt"u8))
            return Invalid("标签签名不匹配。");

        var type = BinaryPrimitives.ReadUInt32BigEndian(header[8..12]);
        if (type == 1)
            return tag.Size >= 48
                ? new IccVcgtSummary("Formula", 3, 0, 0, true, null)
                : Invalid("公式类型数据不足 48 字节。");

        if (type != 0)
            return Invalid($"不支持的 gamma 类型 {type}。");
        if (tag.Size < 18)
            return Invalid("表格类型头不足 18 字节。");

        stream.ReadExactly(header[12..18]);
        var channels = BinaryPrimitives.ReadUInt16BigEndian(header[12..14]);
        var samples = BinaryPrimitives.ReadUInt16BigEndian(header[14..16]);
        var entryBytes = BinaryPrimitives.ReadUInt16BigEndian(header[16..18]);
        if (channels == 0 || samples == 0)
            return Invalid("通道数和每通道采样点数必须大于零。");
        if (entryBytes is not (1 or 2))
            return Invalid($"每个采样值的字节数 {entryBytes} 不受支持。");

        var requiredBytes = 18UL + (ulong)channels * samples * entryBytes;
        if (requiredBytes > tag.Size)
            return Invalid($"采样表长度不足（需要 {requiredBytes} 字节，标签为 {tag.Size} 字节）。");

        return new IccVcgtSummary("Table", channels, samples, (uint)(entryBytes * 8), true, null);
    }

    private static string ReadSignature(ReadOnlySpan<byte> bytes)
    {
        var value = Encoding.ASCII.GetString(bytes);
        return value.All(character => character is >= ' ' and <= '~') ? value : Convert.ToHexString(bytes);
    }

    private static string DescribeSignature(string signature, string description) => signature switch
    {
        "mntr" => "Monitor (mntr)",
        "scnr" => "Scanner (scnr)",
        "prtr" => "Printer (prtr)",
        "link" => "Device link (link)",
        "spac" => "Color space (spac)",
        "abst" => "Abstract (abst)",
        "nmcl" => "Named color (nmcl)",
        "RGB " => "RGB",
        "GRAY" => "Grayscale",
        "CMYK" => "CMYK",
        "XYZ " => "XYZ",
        "Lab " => "Lab",
        "Luv " => "Luv",
        "YCbr" => "YCbCr",
        "Yxy " => "Yxy",
        "HSV " => "HSV",
        "HLS " => "HLS",
        "CMY " => "CMY",
        _ => $"{description}: {signature.Trim()}"
    };

    private static string DescribeOptionalSignature(string signature) =>
        string.IsNullOrWhiteSpace(signature) || signature.All(character => character == ' ' || character == '\0')
            ? "Not specified"
            : signature.Trim();
}
