using System.Buffers.Binary;

namespace Cdmw.ArchiveLite.Core;

public enum DdsCompressedFamily
{
    None,
    Bc1,
    Bc2,
    Bc3,
    Bc4,
    Bc5,
    Bc6h,
    Bc7,
}

/// <summary>
/// The header facts that decide how expensive a DDS is to decode and how much memory the decoded
/// image needs. This deliberately reads far less than <see cref="AssetMetadataInspector"/>, which
/// builds a full human-readable description; only decode cost and resource bounds are modelled here.
/// </summary>
public sealed record DdsTextureHeader(int Width, int Height, int MipCount, DdsCompressedFamily Family)
{
    private const int LegacyHeaderLength = 128;
    private const int Dx10HeaderLength = 148;

    /// <summary>Decoded bytes per pixel the texture helper must hold for this family.</summary>
    public int DecodedBytesPerPixel => Family == DdsCompressedFamily.Bc6h ? 16 : 4;

    public static bool TryRead(string path, out DdsTextureHeader header)
    {
        header = null!;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                Dx10HeaderLength,
                FileOptions.SequentialScan);
            Span<byte> buffer = stackalloc byte[Dx10HeaderLength];
            var read = stream.ReadAtLeast(buffer, LegacyHeaderLength, throwOnEndOfStream: false);
            return read >= LegacyHeaderLength && TryRead(buffer[..read], out header);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out DdsTextureHeader header)
    {
        header = null!;
        if (bytes.Length < LegacyHeaderLength
            || !bytes[..4].SequenceEqual("DDS "u8)
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 124)
        {
            return false;
        }

        var height = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var width = BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return false;
        }
        var mipCount = (int)Math.Clamp(BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]), 1u, 32u);
        var fourCc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[84..]);
        var family = fourCc == 0x30315844u // "DX10"
            ? bytes.Length >= Dx10HeaderLength
                ? DxgiFamily(BinaryPrimitives.ReadUInt32LittleEndian(bytes[128..]))
                : DdsCompressedFamily.None
            : FourCcFamily(fourCc);

        header = new DdsTextureHeader((int)width, (int)height, mipCount, family);
        return true;
    }

    private static DdsCompressedFamily DxgiFamily(uint format) => format switch
    {
        >= 70 and <= 72 => DdsCompressedFamily.Bc1,
        >= 73 and <= 75 => DdsCompressedFamily.Bc2,
        >= 76 and <= 78 => DdsCompressedFamily.Bc3,
        >= 79 and <= 81 => DdsCompressedFamily.Bc4,
        >= 82 and <= 84 => DdsCompressedFamily.Bc5,
        >= 94 and <= 96 => DdsCompressedFamily.Bc6h,
        >= 97 and <= 99 => DdsCompressedFamily.Bc7,
        _ => DdsCompressedFamily.None,
    };

    private static DdsCompressedFamily FourCcFamily(uint fourCc) => fourCc switch
    {
        0x31545844u => DdsCompressedFamily.Bc1,                             // DXT1
        0x32545844u or 0x33545844u => DdsCompressedFamily.Bc2,              // DXT2, DXT3
        0x34545844u or 0x35545844u => DdsCompressedFamily.Bc3,              // DXT4, DXT5
        0x31495441u or 0x55344342u or 0x53344342u => DdsCompressedFamily.Bc4, // ATI1, BC4U, BC4S
        0x32495441u or 0x55354342u or 0x53354342u => DdsCompressedFamily.Bc5, // ATI2, BC5U, BC5S
        _ => DdsCompressedFamily.None,
    };
}
