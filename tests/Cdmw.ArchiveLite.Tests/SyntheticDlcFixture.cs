using System.Buffers.Binary;
using System.Text;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Tests;

internal sealed partial class SyntheticArchiveFixture
{
    internal static async Task<SyntheticArchiveFixture> CreateDlcAsync()
    {
        var fixture = await CreateNameIndexAsync(useStaticInfoNaming: true, currentMountedSources: true);
        var root = Path.Combine(fixture.Root, "dlc", "dlc_01");
        const string table = "gamedata/binarystaticinfo__/bin/";
        const string icon = "itemicon_prefab_ship_stern_01";
        const string prefab = "ship_stern_01";
        var iconHash = ArchiveMetadataReader.ModelNameHash(icon);
        var prefabHash = ArchiveMetadataReader.ModelNameHash(prefab);
        var equipHash = ArchiveMetadataReader.ModelNameHash("ShipStern");
        var ship = NameFirst(BuildItemInfoRow(90001, "Item_Ship_Stern_01", "900010070", "900010071",
            prefabHash, iconHash, 1, equipHash, prefabListLayout: ItemPrefabListLayout.Compact));
        var iconOnly = NameFirst(BuildItemInfoRow(90002, "Item_Ship_Stern_02", "900020070", "900020071",
            0, iconHash, 1, equipHash, prefabListLayout: ItemPrefabListLayout.Compact));
        var updated = NameFirst(BuildItemInfoRow(1234, "Item_Updated_Base", "12340070", "12340071", 0, 0, 1));
        var items = BuildItemInfoTable([(90001, ship), (90002, iconOnly), (1234, updated)]);
        var strings = BuildStringInfoTable([(iconHash, icon), (prefabHash, prefab),
            (ArchiveMetadataReader.ModelNameHash("itemicon_prefab_cd_t9999_empty"), "itemicon_prefab_cd_t9999_empty")]);
        foreach (var row in ArchiveMetadataReader.ResolvePabghDirectory(strings.Directory, strings.Blob)!)
        {
            var at = (int)row.Offset;
            strings.Blob.AsSpan(at, 4).CopyTo(strings.Blob.AsSpan(at + 4));
            strings.Blob.AsSpan(at, 4).Clear();
        }
        var equip = BuildEquipTypeInfoTable([(equipHash, "ShipStern")]);
        var payloads = new List<(string Path, byte[] Bytes)>
        {
            (table + "iteminfo.dlc_01.staticinfobody", items.Blob),
            (table + "iteminfo.dlc_01.staticinfoheader", items.Directory),
            (table + "stringinfo.dlc_01.staticinfobody", strings.Blob),
            (table + "stringinfo.dlc_01.staticinfoheader", strings.Directory),
            (table + "equiptypeinfo.dlc_01.staticinfobody", NameFirst(equip.Blob)),
            (table + "equiptypeinfo.dlc_01.staticinfoheader", equip.Directory),
            ("gamedata/stringtable/binary__/eng/item.dlc_01.paloc", WrapLocalization(BuildLocalization([
                (7, "900010070", "DLC Stern"), (7, "900020070", "Related Stern"), (7, "12340070", "Updated Base Item")]))),
            ("gamedata/stringtable/binary__/ara/item.dlc_01.paloc", WrapLocalization(BuildLocalization([(7, "900010070", "مؤخرة السفينة")]))),
            ("ui/texture/icon/" + icon + ".dds", BuildDecodableDds()),
            ("ui/texture/icon/itemicon_prefab_cd_t9999_empty.dds", BuildDecodableDds()),
            ("character/bin__/ship_stern_01.prefab", Encoding.ASCII.GetBytes("object/ships/nested.prefab\0")),
            ("object/ships/nested.prefab", Encoding.ASCII.GetBytes("object/ships/stern.pami\0")),
            ("object/ships/stern.pami", Encoding.ASCII.GetBytes("object/ships/stern.pam\0object/ships/nested.prefab\0")),
            ("object/ships/stern.pam", [1, 2, 3]),
        };
        AddCharacter(payloads, "dlc_01", 501, "captain", true);
        payloads.Add(("gamedata/stringtable/binary__/eng/character.dlc_01.paloc", WrapLocalization(BuildLocalization([]))));
        await BuildCompactPackageAsync(root, "0001", payloads);
        await WriteCompactMountsAsync(root, ["0001"]);

        var characters = new List<(string Path, byte[] Bytes)>();
        AddCharacter(characters, "", 500, "hero", false);
        characters.Add(("gamedata/stringtable/binary__/eng/character.paloc", WrapLocalization(BuildLocalization([
            (3, ((500UL << 32) | 0x30).ToString(), "Base Hero"),
            (3, ((501UL << 32) | 0x30).ToString(), "Inherited Captain")]))));
        await BuildPackageAsync(fixture.Root, "0043", characters);
        await WriteCompactMountsAsync(fixture.Root, ["0036", "0037", "0038", "0042", "0043", "0008"]);
        return fixture;
    }

    private static byte[] NameFirst(byte[] row)
    {
        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(4));
        return row[4..(8 + length)].Concat(row[..4]).Concat(row[(8 + length)..]).ToArray();
    }

    private static void AddCharacter(List<(string Path, byte[] Bytes)> entries, string suffix, uint id, string name, bool current)
    {
        var path = $"character/appearance/3_npc/{name}.app_xml";
        var key = ((ulong)id << 32 | 0x30).ToString();
        var row = BitConverter.GetBytes(id).Concat(BitConverter.GetBytes((uint)name.Length)).Concat(Encoding.ASCII.GetBytes(name))
            .Concat(new byte[] { 0, 3, 0x30, 0, 0, 0 }).Concat(BitConverter.GetBytes(id))
            .Concat(BitConverter.GetBytes((uint)key.Length)).Concat(Encoding.ASCII.GetBytes(key)).ToArray();
        var info = BuildItemInfoTable([(id, current ? NameFirst(row) : row)]);
        var link = new byte[29];
        BinaryPrimitives.WriteUInt32LittleEndian(link.AsSpan(current ? 4 : 0), id);
        BinaryPrimitives.WriteUInt32LittleEndian(link.AsSpan(17), ArchiveMetadataReader.ModelNameHash(path));
        BinaryPrimitives.WriteUInt32LittleEndian(link.AsSpan(25), id);
        var prefix = "gamedata/binarystaticinfo__/bin/";
        var tag = suffix.Length == 0 ? "" : "." + suffix;
        entries.Add((prefix + "characterinfo" + tag + ".staticinfobody", info.Blob));
        entries.Add((prefix + "characterinfo" + tag + ".staticinfoheader", info.Directory));
        entries.Add((prefix + "characterappearanceindexinfo" + tag + ".staticinfobody", link));
        entries.Add((prefix + "characterappearanceindexinfo" + tag + ".staticinfoheader",
            new byte[] { 1, 0 }.Concat(BitConverter.GetBytes(id)).Concat(new byte[8]).ToArray()));
        entries.Add((path, Encoding.ASCII.GetBytes($"<Appearance><Nude Name=\"{name}_body_0001\"/></Appearance>")));
        entries.Add(($"character/model/3_npc/nude/{name}_body_0001.pac", BuildSyntheticPac()));
    }

    internal static async Task WriteCompactMountsAsync(string root, string[] names)
    {
        using var output = new MemoryStream();
        output.Write([0, 0, (byte)names.Length, 2]);
        uint offset = 0;
        foreach (var name in names)
        {
            WriteUInt32(output, 0); WriteUInt32(output, offset); WriteUInt32(output, 0);
            offset += (uint)Encoding.ASCII.GetByteCount(name) + 1;
        }
        WriteUInt32(output, offset);
        foreach (var name in names) { output.Write(Encoding.ASCII.GetBytes(name)); output.WriteByte(0); }
        output.Write(new byte[64]);
        Directory.CreateDirectory(Path.Combine(root, "meta"));
        await File.WriteAllBytesAsync(Path.Combine(root, "meta", "0.papgt"), output.ToArray());
    }

    private static async Task BuildCompactPackageAsync(string root, string package, IReadOnlyList<(string Path, byte[] Bytes)> payloads)
    {
        var directory = Path.Combine(root, package);
        Directory.CreateDirectory(directory);
        var extensions = new[] { "" }.Concat(payloads.Select(p => Path.GetExtension(p.Path)).Distinct()).ToArray();
        using var names = new MemoryStream();
        using var data = new MemoryStream();
        using var table = new MemoryStream();
        foreach (var (path, bytes) in payloads)
        {
            WriteUInt32(table, (uint)names.Position);
            names.WriteByte(0);
            var stem = Encoding.ASCII.GetBytes(path[..^Path.GetExtension(path).Length]);
            names.WriteByte((byte)stem.Length); names.Write(stem);
            WriteUInt32(table, (uint)data.Position); WriteUInt32(table, (uint)bytes.Length); WriteUInt32(table, (uint)bytes.Length);
            WriteUInt16(table, 20); WriteUInt16(table, (ushort)(Array.IndexOf(extensions, Path.GetExtension(path)) << 8));
            data.Write(bytes);
        }
        using var pamt = new MemoryStream();
        WriteUInt32(pamt, 0); WriteUInt32(pamt, 1); WriteUInt32(pamt, 0);
        WriteUInt32(pamt, 20); WriteUInt32(pamt, 0); WriteUInt32(pamt, 0);
        WriteUInt32(pamt, 0);
        WriteUInt32(pamt, (uint)names.Length); pamt.Write(names.ToArray());
        WriteUInt32(pamt, 0); WriteUInt32(pamt, (uint)payloads.Count); pamt.Write(table.ToArray());
        var trailer = Encoding.ASCII.GetBytes(string.Join('\0', extensions) + "\0");
        WriteUInt32(pamt, (uint)trailer.Length); pamt.Write(trailer);
        await File.WriteAllBytesAsync(Path.Combine(directory, "0.pamt"), pamt.ToArray());
        await File.WriteAllBytesAsync(Path.Combine(directory, "20.paz"), data.ToArray());
    }
}
