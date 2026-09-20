using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Tests;

internal static partial class ArchiveLiteTestRunner
{
    private static async Task TestCurrentItemCatalogAsync()
    {
        var literal = System.Text.Encoding.UTF8.GetBytes(new string('x', 1100));
        Require(ArchiveMetadataReader.LocalizationPayload(SyntheticArchiveFixture.WrapLocalization(literal)).SequenceEqual(literal),
            "wrapped PALOC extended literal lengths decoded incorrectly");
        var matched = SyntheticArchiveFixture.WrapLocalization(new byte[14])[..512]
            .Concat(new byte[] { 0x32, 97, 98, 99, 3, 0, 0x50, 120, 121, 122, 117, 118 }).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(matched.AsSpan(9), 12);
        Require(System.Text.Encoding.ASCII.GetString(ArchiveMetadataReader.LocalizationPayload(matched)) == "abcabcabcxyzuv",
            "overlapping PALOC LZ4 matches decoded incorrectly");
        var unsupported = (byte[])matched.Clone(); unsupported[5] = 1;
        var oversized = (byte[])matched.Clone();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(13), uint.MaxValue);
        var invalidOffset = (byte[])matched.Clone(); invalidOffset[516] = 0;
        foreach (var damaged in new[] { matched[..510], matched[..^1], unsupported, oversized, invalidOffset })
        {
            var rejectedContainer = false;
            try { ArchiveMetadataReader.LocalizationPayload(damaged); } catch (InvalidDataException) { rejectedContainer = true; }
            Require(rejectedContainer, "a malformed PALOC wrapper was accepted");
        }
        await using var fixture = await SyntheticArchiveFixture.CreateNameIndexAsync(
            useStaticInfoNaming: true, currentMountedSources: true).ConfigureAwait(false);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(new(fixture.Root), CancellationToken.None).ConfigureAwait(false);
        var builder = new ArchiveItemNameIndexService(sessions, native);
        var built = await builder.BuildAsync(new(opened.SessionId), null, CancellationToken.None).ConfigureAwait(false);
        Require(built.Available && built.Warning is null && built.ItemCount == 3,
            $"current mounted catalogue dropped inventory records: {built.ItemCount}, {built.Warning}");
        var search = new ArchiveItemCatalogService(sessions, builder);
        var linked = await search.SearchAsync(new(opened.SessionId, "1234"), CancellationToken.None).ConfigureAwait(false);
        Require(linked.Items.Single().PacFiles.Contains("cd_test_01_sword.pac"),
            "model references outside legacy package 0009 disappeared from the active catalogue");
        var letter = await search.SearchAsync(new(opened.SessionId, "Lost Letter"), CancellationToken.None).ConfigureAwait(false);
        Require(letter.Items.Single().ItemId == 9012 && letter.Items[0].PacFiles.Count == 0,
            "a localized item without a model is missing or has fabricated model links");
        var arabic = await search.SearchAsync(new(opened.SessionId, "رسالة"), CancellationToken.None).ConfigureAwait(false);
        Require(arabic.Items.Single().ItemId == 9012, "a discovered language outside the old fixed list is unsearchable");

        using var second = new ArchiveSessionManager(native);
        var reopened = await second.OpenAsync(new(fixture.Root), CancellationToken.None).ConfigureAwait(false);
        var reloaded = new ArchiveItemNameIndexService(second, native);
        var cached = await reloaded.BuildAsync(new(reopened.SessionId), null, CancellationToken.None).ConfigureAwait(false);
        Require(cached.UsedCache && cached.ItemCount == 3, "current catalogue cache did not preserve all item IDs");
        Require((await new ArchiveItemCatalogService(second, reloaded).SearchAsync(
            new(reopened.SessionId, "رسالة"), CancellationToken.None).ConfigureAwait(false)).Items.Single().ItemId == 9012,
            "localized search did not survive persistent cache loading");

        var session = sessions.GetRequired(opened.SessionId);
        var selector = new ArchiveItemSources(fixture.Root);
        for (long i = 0; i < session.Index.EntryCount; i++) selector.Offer(session.Index.ReadEntry(i));
        selector.Finish();
        Require(Path.GetFileName(Path.GetDirectoryName(selector.ItemInfo!.SourcePamt)) == "0036"
            && selector.ItemInfo.SourcePamt == selector.ItemInfoHeader!.SourcePamt,
            "obsolete or unmounted tables replaced the complete active table pair");
        var broken = new ArchiveItemSources(fixture.Root);
        broken.Offer(selector.ItemInfo);
        broken.Offer(selector.ItemInfoHeader! with { SourcePamt = selector.Localizations["eng"].SourcePamt });
        var rejected = false;
        try { broken.Finish(); } catch (InvalidDataException) { rejected = true; }
        Require(rejected, "a current table body and header from different packages were combined");

        var mount = Path.Combine(fixture.Root, "meta", "0.papgt");
        var written = File.GetLastWriteTimeUtc(mount);
        await fixture.WriteMountOrderAsync(["0037", "0036", "0038", "0042", "0008"]).ConfigureAwait(false);
        File.SetLastWriteTimeUtc(mount, written);
        var changed = await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None).ConfigureAwait(false);
        Require(changed.Value != opened.Fingerprint, "mount order changed without invalidating the catalogue fingerprint");
    }
}
