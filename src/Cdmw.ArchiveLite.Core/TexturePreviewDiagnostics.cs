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

    /// <summary>
    /// Set once by the hosting process to forward each failure as it happens. The ring alone is
    /// only readable in-process, and the process that records these is not the one a user reads.
    /// </summary>
    public static Action<TextureDecodeFailure>? Sink { get; set; }

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
        try
        {
            Sink?.Invoke(record);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Reporting a failure must never replace the failure being reported.
        }
    }

    /// <summary>
    /// Renders one failure as a single line. Helper output is multi-line, and the transport that
    /// carries this to the user's log is line-oriented.
    /// </summary>
    public static string Describe(TextureDecodeFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return "texture decode failed:"
            + $" reason={Flatten(failure.Reason)}"
            + $" operation={Flatten(failure.Operation)}"
            + $" source={Flatten(failure.SourcePath)}"
            + $" detail={Flatten(failure.Detail)}";
    }

    private static string Flatten(string? value)
    {
        var text = (value ?? string.Empty).ReplaceLineEndings(" ").Trim();
        return string.IsNullOrEmpty(text) ? "(none)" : text;
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
