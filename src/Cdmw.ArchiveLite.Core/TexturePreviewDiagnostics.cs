namespace Cdmw.ArchiveLite.Core;

public sealed record TextureDecodeFailure(
    DateTimeOffset TimestampUtc,
    string Operation,
    string Reason,
    string SourcePath,
    string Detail);

/// <summary>
/// A bounded process-wide record of texture decode failures. Preview failures are otherwise thrown
/// and lost, which leaves nothing to inspect when a decode fails intermittently or only for one
/// archive.
/// </summary>
public static class TexturePreviewDiagnostics
{
    private const int MaximumRecords = 128;
    private const int MaximumDetailLength = 2000;
    private static readonly Queue<TextureDecodeFailure> Records = new();
    private static readonly Lock Gate = new();

    public static void RecordFailure(string operation, string reason, string sourcePath, string detail)
    {
        var record = new TextureDecodeFailure(
            DateTimeOffset.UtcNow,
            operation ?? string.Empty,
            reason ?? string.Empty,
            sourcePath ?? string.Empty,
            Summarize(detail));
        lock (Gate)
        {
            Records.Enqueue(record);
            while (Records.Count > MaximumRecords)
            {
                Records.Dequeue();
            }
        }
    }

    public static IReadOnlyList<TextureDecodeFailure> Failures(bool clear = false)
    {
        lock (Gate)
        {
            var snapshot = Records.ToArray();
            if (clear)
            {
                Records.Clear();
            }
            return snapshot;
        }
    }

    /// <summary>Keeps the tail, which is where a helper writes the reason it stopped.</summary>
    private static string Summarize(string? detail)
    {
        var text = (detail ?? string.Empty).Trim();
        return text.Length <= MaximumDetailLength ? text : text[^MaximumDetailLength..];
    }
}
