using System.Buffers.Binary;
using System.Text;
using MonitorControl.Core.Models;

namespace MonitorControl.Core.Services;

/// <summary>Creates a new SDR RGB gain profile with a VCGT table, preserving the source ICC profile.</summary>
public static class IccRgbGainProfileWriter
{
    private const int HeaderSize = 128;
    private const int TagEntrySize = 12;
    private const uint MaximumProfileBytes = 64 * 1024 * 1024;
    private const ushort SampleCount = 256;

    public static string Create(string sourcePath, string destinationPath, RgbGainAdjustment gains)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(gains);
        gains.Validate();

        var inspection = IccProfileInspector.Inspect(sourcePath);
        if (!inspection.ProfileClass.StartsWith("Monitor", StringComparison.Ordinal) || inspection.ColorSpace != "RGB")
            throw new InvalidDataException("RGB gain profiles can only be generated from an RGB monitor ICC profile.");
        if (inspection.Mhc2Tag is not null)
            throw new InvalidDataException("This profile contains MHC2 calibration. VCGT must not be combined with MHC2 in this version.");
        if (inspection.VcgtSummary is { IsStructurallyValid: false })
            throw new InvalidDataException($"The source profile's VCGT data is malformed: {inspection.VcgtSummary.ValidationMessage}");
        if (inspection.VcgtSummary is { Encoding: "Formula" })
            throw new InvalidDataException("Formula-based VCGT profiles cannot yet be safely combined with RGB gain adjustments.");
        if (inspection.VcgtSummary is { Encoding: "Table" } tableSummary &&
            (tableSummary.ChannelCount != 3 || tableSummary.EntryBits is not (8 or 16)))
            throw new InvalidDataException("The source VCGT table must have three RGB channels with 8-bit or 16-bit entries.");

        var source = File.ReadAllBytes(sourcePath);
        var originalVcgt = inspection.VcgtTag is { } sourceVcgtTag && inspection.VcgtSummary is { Encoding: "Table" } sourceVcgtSummary
            ? source.AsSpan(checked((int)sourceVcgtTag.Offset), checked((int)sourceVcgtTag.Size)).ToArray()
            : null;
        var tags = inspection.Tags.Where(tag => tag.Signature is not "vcgt" and not "mcRG").ToList();
        var outputTagCount = checked(tags.Count + 2);
        if (outputTagCount > 4096) throw new InvalidDataException("The source profile has too many tags to add RGB calibration metadata.");

        var vcgt = CreateVcgt(gains, originalVcgt, inspection.VcgtSummary);
        var gainMetadata = CreateGainMetadata(gains);
        using var output = new MemoryStream(source.Length + vcgt.Length + gainMetadata.Length + outputTagCount * TagEntrySize + 64);
        output.Write(source, 0, HeaderSize);
        Span<byte> uintBuffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(uintBuffer, (uint)outputTagCount);
        output.Write(uintBuffer);
        output.Position = HeaderSize + 4L + (long)outputTagCount * TagEntrySize;

        var entries = new List<(string Signature, uint Offset, uint Size)>(outputTagCount);
        foreach (var tag in tags)
        {
            Align4(output);
            var offset = checked((uint)output.Position);
            output.Write(source, checked((int)tag.Offset), checked((int)tag.Size));
            entries.Add((tag.Signature, offset, tag.Size));
        }

        Align4(output);
        var vcgtOffset = checked((uint)output.Position);
        output.Write(vcgt);
        entries.Add(("vcgt", vcgtOffset, checked((uint)vcgt.Length)));
        Align4(output);
        var metadataOffset = checked((uint)output.Position);
        output.Write(gainMetadata);
        entries.Add(("mcRG", metadataOffset, checked((uint)gainMetadata.Length)));

        if (output.Length > MaximumProfileBytes) throw new InvalidDataException("The generated ICC profile exceeds the 64 MB safety limit.");
        var finalSize = checked((uint)output.Length);
        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), finalSize);
        for (var index = 0; index < entries.Count; index++)
        {
            var entryOffset = HeaderSize + 4 + index * TagEntrySize;
            Encoding.ASCII.GetBytes(entries[index].Signature, bytes.AsSpan(entryOffset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryOffset + 4, 4), entries[index].Offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entryOffset + 8, 4), entries[index].Size);
        }
        // The profile ID refers to the old payload and must not survive a content change.
        bytes.AsSpan(84, 16).Clear();

        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var temporaryPath = fullDestination + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, fullDestination, overwrite: true);
        _ = IccProfileInspector.Inspect(fullDestination);
        return fullDestination;
    }

    private static byte[] CreateVcgt(RgbGainAdjustment gains, byte[]? originalVcgt, IccVcgtSummary? originalSummary)
    {
        var result = new byte[18 + 3 * SampleCount * 2];
        "vcgt"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(14, 2), SampleCount);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(16, 2), 2);
        var gainsPercent = new[] { gains.RedPercent, gains.GreenPercent, gains.BluePercent };
        for (var channel = 0; channel < gainsPercent.Length; channel++)
        {
            for (var sample = 0; sample < SampleCount; sample++)
            {
                var input = sample / (double)(SampleCount - 1);
                var baseValue = originalVcgt is null || originalSummary is null
                    ? input
                    : SampleOriginalTable(originalVcgt, originalSummary, channel, input);
                var adjusted = Math.Clamp(baseValue * gainsPercent[channel] / 100.0, 0.0, 1.0);
                var value = checked((ushort)Math.Round(adjusted * ushort.MaxValue, MidpointRounding.AwayFromZero));
                var offset = 18 + ((channel * SampleCount + sample) * 2);
                BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), value);
            }
        }
        return result;
    }

    private static double SampleOriginalTable(byte[] table, IccVcgtSummary summary, int channel, double input)
    {
        var position = input * (summary.SamplesPerChannel - 1);
        var lower = (uint)Math.Floor(position);
        var upper = Math.Min(lower + 1, summary.SamplesPerChannel - 1);
        var fraction = position - lower;
        var first = ReadOriginalSample(table, summary, channel, lower);
        var second = ReadOriginalSample(table, summary, channel, upper);
        return (first + (second - first) * fraction) / ushort.MaxValue;
    }

    private static ushort ReadOriginalSample(byte[] table, IccVcgtSummary summary, int channel, uint sample)
    {
        var bytesPerSample = summary.EntryBits / 8;
        var offset = checked(18 + (int)(((uint)channel * summary.SamplesPerChannel + sample) * bytesPerSample));
        return bytesPerSample == 1
            ? (ushort)(table[offset] * 257)
            : BinaryPrimitives.ReadUInt16BigEndian(table.AsSpan(offset, 2));
    }

    private static byte[] CreateGainMetadata(RgbGainAdjustment gains)
    {
        var result = new byte[16];
        "mcRG"u8.CopyTo(result);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(8, 2), checked((ushort)gains.RedPercent));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(10, 2), checked((ushort)gains.GreenPercent));
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(12, 2), checked((ushort)gains.BluePercent));
        return result;
    }

    private static void Align4(Stream stream)
    {
        while ((stream.Position & 3) != 0) stream.WriteByte(0);
    }
}
