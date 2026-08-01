using System.Buffers.Binary;

namespace Cdmw.ArchiveLite.Core;

internal static class NativePreviewGeometryIO
{
    public const int FloatsPerPreviewVertex = 23;
    public const int BytesPerPreviewVertex = FloatsPerPreviewVertex * sizeof(float);
    public const int RecordsPerChunk = 98_304;

    public static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        256 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static FileStream OpenNew(string path) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        256 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    public static float ReadFiniteSingle(byte[] input, int offset, string label)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(input.AsSpan(offset, sizeof(float)));
        if (!float.IsFinite(value))
        {
            throw new InvalidDataException($"Native preview geometry contains a non-finite {label} value.");
        }
        return value;
    }
}
