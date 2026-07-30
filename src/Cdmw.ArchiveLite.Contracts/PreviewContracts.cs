namespace Cdmw.ArchiveLite.Contracts;

public enum PreviewKind
{
    Metadata,
    Text,
    Image,
    Audio,
    Video,
    Model,
    Hkx,
    StructuredData,
    Hex,
}

public sealed record PreviewRequest(
    string SessionId,
    long EntryId,
    int TextCharacterLimit = 120_000,
    int BinaryByteLimit = 256,
    bool IncludeModelTextures = false,
    int TrackIndex = 0);

/// <summary>
/// One playable sound inside a container that holds several, such as a Wwise sound bank.
/// </summary>
/// <param name="Index">The one-based position the decoder uses to select this sound.</param>
/// <param name="Name">The sound's own identity, which for a bank is its Wwise source id.</param>
public sealed record PreviewTrack(int Index, string Name, long Size);

public sealed record PreviewResult(
    string SessionId,
    long EntryId,
    PreviewKind Kind,
    string Title,
    string Metadata,
    string? Text = null,
    string? ArtifactPath = null,
    string? MediaKind = null,
    IReadOnlyList<string>? Warnings = null,
    string? Syntax = null,
    IReadOnlyList<PreviewTrack>? Tracks = null,
    int TrackIndex = 0);
