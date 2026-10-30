using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Tests;

internal static partial class ArchiveLiteTestRunner
{
    private static async Task TestDlcCompatibilityAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateDlcAsync();
        var before = await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None);
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(new(fixture.Root), CancellationToken.None);
        var session = sessions.GetRequired(opened.SessionId);
        var compact = session.Index.FindEntriesByPath("object/ships/stern.pam", 1).Single();
        Require(compact.PazIndex == 20 && !compact.IsEncrypted && compact.Flags >= 256,
            "compact extension flags were confused with encryption or sparse PAZ IDs were lost");
        Require(native.Decode(compact).Bytes.SequenceEqual(new byte[] { 1, 2, 3 }), "compact archive payload changed");
        var builder = new ArchiveItemNameIndexService(sessions, native);
        var built = await builder.BuildAsync(new(session.Id), null, CancellationToken.None);
        Require(built.Available && built.Warning is null && built.ItemCount == 5, $"DLC merge failed: {built.Warning}, {built.ItemCount}");
        var search = new ArchiveItemCatalogService(sessions, builder);
        var dlc = await search.SearchAsync(new(session.Id, OnlyDlc: true), CancellationToken.None);
        Require(dlc.TotalMatches == 2 && dlc.Items.All(i => i.IsDlc && i.Category == "Ships" && i.Group == "Sterns")
            && dlc.Categories.Sum(c => c.Count) == 2, "DLC-only items or category counts include a base-game override");
        var ship = dlc.Items.Single(i => i.ItemId == 90001);
        Require(ship.IconPaths.Single().EndsWith("itemicon_prefab_ship_stern_01.dds")
            && ship.PacFiles.Contains("object/ships/stern.pam"), "non-cd ship icon or nested prefab/PAMI model disappeared");
        var translated = await search.SearchAsync(new(session.Id, "مؤخرة السفينة"), CancellationToken.None);
        Require(translated.Items.Single().ItemId == 90001, "compressed DLC localization was not merged");
        var baseItem = await search.SearchAsync(new(session.Id, "Updated Base Item"), CancellationToken.None);
        Require(baseItem.Items.Single().ItemId == 1234 && !baseItem.Items[0].IsDlc, "DLC override lost base item identity");
        Require(session.TryGetNameIndex(out var names), "item names were not published");
        Require(names!.Enrich(compact).KnownName.Contains("DLC Stern") && !names.Enrich(compact).KnownName.Contains("Related Stern"),
            "icon-derived prefab link was promoted to exact ownership");

        var characters = new ArchiveCharacterCatalogService(sessions, native);
        await characters.BuildAsync(new(session.Id), null, CancellationToken.None);
        var captain = await characters.SearchAsync(new(session.Id, Query: "Inherited Captain", View: "appearances", Tab: "all"), null, CancellationToken.None);
        var hero = await characters.SearchAsync(new(session.Id, Query: "Base Hero", View: "appearances", Tab: "all"), null, CancellationToken.None);
        Require(captain.Rows.Count == 1 && hero.Rows.Count == 1, "base names or DLC character links were lost");
        var captainModel = session.Index.FindEntriesByPath("character/model/3_npc/nude/captain_body_0001.pac", 1).Single();
        var preview = await new NativeModelPreviewService().BuildAsync(session, captainModel, null, CancellationToken.None);
        Require(File.Exists(Path.Combine(preview, "manifest.json")), "compact DLC model did not reach the native preview package");

        using var reloaded = new ArchiveSessionManager(native);
        var reopened = await reloaded.OpenAsync(new(fixture.Root), CancellationToken.None);
        var cacheBuilder = new ArchiveItemNameIndexService(reloaded, native);
        Require((await cacheBuilder.BuildAsync(new(reopened.SessionId), null, CancellationToken.None)).UsedCache,
            "DLC catalogue cache was not reused");
        var cached = await new ArchiveItemCatalogService(reloaded, cacheBuilder).SearchAsync(new(reopened.SessionId, OnlyDlc: true), CancellationToken.None);
        Require(cached.TotalMatches == 2 && cached.Items.Single(i => i.ItemId == 90001).PacFiles.Contains("object/ships/stern.pam"),
            "DLC flags or nested model paths were lost after cache reload");
        Require((await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None)).Value == before.Value, "reading DLC changed source archives");
        var mount = Path.Combine(fixture.Root, "dlc", "dlc_01", "meta", "0.papgt");
        var timestamp = File.GetLastWriteTimeUtc(mount);
        await SyntheticArchiveFixture.WriteCompactMountsAsync(Path.Combine(fixture.Root, "dlc", "dlc_01"), ["0002"]);
        File.SetLastWriteTimeUtc(mount, timestamp);
        Require((await ArchiveFingerprint.ComputeAsync(fixture.Root, CancellationToken.None)).Value != before.Value,
            "DLC mount content changed without invalidating caches");

        Directory.CreateDirectory(fixture.OutputRoot);
        var source = await File.ReadAllBytesAsync(compact.SourcePamt);
        var badTerminator = (byte[])source.Clone(); badTerminator[^1] = 1;
        var badParent = (byte[])source.Clone(); badParent[32] = 1;
        foreach (var malformed in new[] { source[..^1], badTerminator, badParent })
        {
            var path = Path.Combine(fixture.OutputRoot, "invalid.pamt");
            await File.WriteAllBytesAsync(path, malformed);
            var rejected = false;
            try { native.BuildIndex(path, Path.Combine(fixture.OutputRoot, "invalid.ali")); }
            catch (NativeArchiveException) { rejected = true; }
            Require(rejected, "malformed compact names or extensions were accepted");
        }

        byte[] compressed = [0x4f, 65, 66, 67, 68, 4, 0, 0xe4, 0x50, 68, 65, 66, 67, 68];
        foreach (var extension in new[] { ".pam", ".pamlod" })
        {
            var offset = extension == ".pam" ? 0x50 : 0x60;
            var header = new byte[offset];
            void Put(int at, uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), value);
            if (extension == ".pam")
            {
                "PAR "u8.CopyTo(header); Put(4, 0x01001806); Put(0x3c, (uint)offset); Put(0x40, 256); Put(0x44, (uint)compressed.Length);
            }
            else { Put(0, 1); Put(4, (uint)offset); Put(0x50, (uint)offset); Put(0x54, 256); Put(0x58, (uint)compressed.Length); }
            var path = Path.Combine(fixture.OutputRoot, "static" + extension);
            await File.WriteAllBytesAsync(path, header.Concat(compressed).ToArray());
            var decoded = native.Decode(compact with { Path = "object/static" + extension, Extension = extension,
                PazFile = path, Offset = 0, StoredSize = offset + compressed.Length, OriginalSize = offset + 256, Flags = 0x101 }).Bytes;
            Require(decoded.Length == offset + 256 && decoded[offset..].Select((value, i) => value == "ABCD"[i % 4]).All(static value => value),
                "partial DLC static mesh changed its geometry bytes");
        }

        foreach (var (name, slot, category, group) in new[] {
            ("Item_Ship_Stern_01", "ShipStern", "Ships", "Sterns"),
            ("Recipe_Riding_SmallShip_II", "", "Ships", "Blueprint Parts"),
            ("Recipe_Item_TurtleShip_Cannon", "", "Ships", "Equipment Blueprints"),
            ("Item_Skill_AbyssGear_Head", "Helm", "Progression / Reward", "Skill"),
            ("Item_Stat_AbyssGear_Head", "Helm", "Progression / Reward", "Stat"),
            ("Trade_Ore_PackedInVehicle", "", "Trade Goods", "Packaged Cargo"),
        })
        {
            var row = ArchiveItemCatalog.FromRecords([new(1, name, name, [], [], [], [], [], EquipType: slot)]).Items.Single();
            Require(row.Category == category && row.Group == group, $"DLC item definition misclassified: {name}");
        }
        var message = System.Text.Json.JsonSerializer.Serialize(new ItemCatalogSearchRequest("test", OnlyDlc: true), WorkerProtocol.JsonOptions);
        Require(message.Contains("\"only_dlc\":true") && !new ItemCatalogSearchRequest("test").OnlyDlc, "DLC filter protocol changed its default");
    }
}
