namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Bounds a DDS source must satisfy before the texture helper is started. Rejecting an unusable
/// input here costs a header read instead of a process launch plus a decode that would fail or
/// exhaust memory anyway.
/// </summary>
public static class DdsResourceLimits
{
    public const int MaximumDimension = 16_384;
    public const long MaximumPayloadBytes = 512L * 1024L * 1024L;
    public const long MaximumDecodedBytes = 512L * 1024L * 1024L;

    /// <summary>Returns null when the source is safe to decode, or the reason it was rejected.</summary>
    public static string? DescribeRejection(string ddsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ddsPath);
        long sourceLength;
        try
        {
            var info = new FileInfo(ddsPath);
            if (!info.Exists)
            {
                return "The DDS source no longer exists.";
            }
            sourceLength = info.Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "The DDS source could not be opened.";
        }

        if (sourceLength > MaximumPayloadBytes)
        {
            return $"The DDS source is {sourceLength:N0} bytes and exceeds the {MaximumPayloadBytes:N0}-byte limit.";
        }
        if (!DdsTextureHeader.TryRead(ddsPath, out var header))
        {
            return "The DDS header could not be read.";
        }
        if (header.Width > MaximumDimension || header.Height > MaximumDimension)
        {
            return $"The DDS is {header.Width:N0}x{header.Height:N0} and exceeds the {MaximumDimension:N0}px limit.";
        }
        return TryComputeDecodedBytes(header, out var decodedBytes)
            ? null
            : $"The decoded DDS needs more than the {MaximumDecodedBytes:N0}-byte limit ({decodedBytes:N0} bytes and counting).";
    }

    /// <summary>Accumulates every mip level, stopping as soon as the running total breaches the limit.</summary>
    public static bool TryComputeDecodedBytes(DdsTextureHeader header, out long decodedBytes)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (header.Width > MaximumDimension || header.Height > MaximumDimension)
        {
            // Keeps the running total below the range where the per-level product could overflow.
            decodedBytes = long.MaxValue;
            return false;
        }
        var bytesPerPixel = header.DecodedBytesPerPixel;
        long width = header.Width;
        long height = header.Height;
        decodedBytes = 0;
        for (var level = 0; level < header.MipCount; level++)
        {
            var levelBytes = width * height * bytesPerPixel;
            if (levelBytes > MaximumDecodedBytes - decodedBytes)
            {
                decodedBytes += levelBytes;
                return false;
            }
            decodedBytes += levelBytes;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
        return true;
    }
}
