using System.Buffers.Binary;
using System.Text;

namespace Cdmw.ArchiveLite.Tests;

internal sealed class SyntheticArchiveFixture : IAsyncDisposable
{
    private SyntheticArchiveFixture(string root)
    {
        Root = root;
        Pamt = Path.Combine(root, "base", "0.pamt");
        Paz = Path.Combine(root, "base", "0.paz");
        Pathc = Path.Combine(root, "meta", "0.pathc");
        OutputRoot = root + "-output";
    }

    public string Root { get; }
    public string Pamt { get; }
    public string Paz { get; }
    public string Pathc { get; }
    public string OutputRoot { get; }

    public static async Task<SyntheticArchiveFixture> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = new SyntheticArchiveFixture(root);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Pamt)!);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Pathc)!);
        var plainText = Encoding.UTF8.GetBytes("Hello Crimson\nline 2");
        var materialText = Encoding.UTF8.GetBytes("material alpha");
        var materialLz4 = EncodeLz4Literal(materialText);
        var binary = new byte[] { 0, 1, 2, 3, 4, 5, 0xFF };
        var (partialDds, pathc) = BuildPartialDds();
        var payloads = new[] { plainText, materialLz4, binary, partialDds };
        await using (var stream = new FileStream(fixture.Paz, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            foreach (var payload in payloads) await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        var entries = new[]
        {
            new EntrySpec("text/hello.txt", 0u, (uint)plainText.Length, (uint)plainText.Length, 0),
            new EntrySpec("materials/sample.material", (uint)plainText.Length, (uint)materialLz4.Length, (uint)materialText.Length, 2),
            new EntrySpec("binary/blob.bin", (uint)(plainText.Length + materialLz4.Length), (uint)binary.Length, (uint)binary.Length, 0),
            new EntrySpec(
                "texture/test.dds",
                (uint)(plainText.Length + materialLz4.Length + binary.Length),
                (uint)partialDds.Length,
                0x88,
                1),
        };
        await File.WriteAllBytesAsync(fixture.Pamt, BuildPamt(entries)).ConfigureAwait(false);
        await File.WriteAllBytesAsync(fixture.Pathc, pathc).ConfigureAwait(false);
        return fixture;
    }

    public static async Task<SyntheticArchiveFixture> CreateAssociatedAssetsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-associations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = new SyntheticArchiveFixture(root);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Pamt)!);

        var payloads = new (string Path, byte[] Bytes)[]
        {
            ("character/model/hero.pac", BuildSyntheticPac()),
            (
                "character/modelproperty/hero.pac_xml",
                Encoding.UTF8.GetBytes(
                    "<material><texture path=\"character/texture/hero_body_d.dds\" />"
                    + "<texture path=\"character/texture/hero_body_n.dds\" />"
                    + "<physics path=\"character/physics/hero.hkx\" /></material>")),
            ("character/texture/hero_body_d.dds", "DDS synthetic diffuse"u8.ToArray()),
            ("character/texture/hero_body_n.dds", "DDS synthetic normal"u8.ToArray()),
            ("character/physics/hero.hkx", [0x48, 0x4B, 0x58, 0x00]),
            ("character/model/hero.meshinfo", Encoding.UTF8.GetBytes("mesh metadata")),
            ("character/model/hero.prefab", Encoding.UTF8.GetBytes("prefab metadata")),
            ("unrelated/other.dds", "DDS unrelated"u8.ToArray()),
        };

        var entries = new List<EntrySpec>(payloads.Length);
        uint offset = 0;
        await using (var stream = new FileStream(fixture.Paz, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            foreach (var payload in payloads)
            {
                await stream.WriteAsync(payload.Bytes).ConfigureAwait(false);
                entries.Add(new EntrySpec(
                    payload.Path,
                    offset,
                    checked((uint)payload.Bytes.Length),
                    checked((uint)payload.Bytes.Length),
                    0));
                offset = checked(offset + (uint)payload.Bytes.Length);
            }
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        await File.WriteAllBytesAsync(fixture.Pamt, BuildPamt(entries)).ConfigureAwait(false);
        return fixture;
    }

    /// <summary>
    /// A level that names files whose extensions the association vocabulary used to either clip to a
    /// shorter format or not know at all. Every name it references has a decoy beside it that carries
    /// the clipped extension, so resolving the wrong one is visible rather than merely unproven.
    /// </summary>
    public static async Task<SyntheticArchiveFixture> CreateAssociationVocabularyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-vocabulary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = new SyntheticArchiveFixture(root);
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Pamt)!);

        var level = Encoding.UTF8.GetBytes(
            "<level><collision path=\"world/track.paccd\" /><gimmick path=\"world/track.pampg\" />"
            + "<mesh path=\"world/mesh.pat\" /><surface path=\"shader/surface.material\" />"
            + "<motion path=\"motion/walk.pai\" /><prop path=\"props/crate.prefab_xml\" />"
            + "<audio path=\"banks/music.bnk\" /></level>");
        var payloads = new (string Path, byte[] Bytes)[]
        {
            ("world/city.palevel", level),

            // The referenced files.
            ("world/track.paccd", "collision"u8.ToArray()),
            ("world/track.pampg", "gimmick"u8.ToArray()),
            ("world/mesh.pat", "PAT mesh"u8.ToArray()),
            ("shader/surface.material", "surface shader"u8.ToArray()),
            ("motion/walk.pai", "motion"u8.ToArray()),
            ("props/crate.prefab_xml", "<prefab />"u8.ToArray()),
            ("banks/music.bnk", BuildSourceIdSoundBank(SoundBankSourceId)),
            ($"stream/{SoundBankSourceId}.wem", "streamed sound"u8.ToArray()),

            // The decoys: each is what a clipped extension would have resolved to instead.
            ("world/track.pac", "decoy model"u8.ToArray()),
            ("world/track.pam", "decoy model"u8.ToArray()),
            ("props/crate.prefab", "decoy prefab"u8.ToArray()),
        };

        var entries = new List<EntrySpec>(payloads.Length);
        uint offset = 0;
        await using (var stream = new FileStream(fixture.Paz, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            foreach (var payload in payloads)
            {
                await stream.WriteAsync(payload.Bytes).ConfigureAwait(false);
                entries.Add(new EntrySpec(
                    payload.Path,
                    offset,
                    checked((uint)payload.Bytes.Length),
                    checked((uint)payload.Bytes.Length),
                    0));
                offset = checked(offset + (uint)payload.Bytes.Length);
            }
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        await File.WriteAllBytesAsync(fixture.Pamt, BuildPamt(entries)).ConfigureAwait(false);
        return fixture;
    }

    /// <summary>The Wwise source id the synthetic bank lists and the streamed sound is named after.</summary>
    public const uint SoundBankSourceId = 771294;

    /// <summary>A bank whose DIDX table names one sound and whose DATA chunk carries it.</summary>
    private static byte[] BuildSourceIdSoundBank(uint sourceId)
    {
        var sound = "synthetic sound payload"u8.ToArray();
        using var bank = new MemoryStream();
        bank.Write("BKHD"u8);
        WriteUInt32(bank, 24);
        WriteUInt32(bank, 0x8C);
        WriteUInt32(bank, 0x12345678);
        bank.Write(new byte[16]);
        bank.Write("DIDX"u8);
        WriteUInt32(bank, 12);
        WriteUInt32(bank, sourceId);
        WriteUInt32(bank, 0);
        WriteUInt32(bank, checked((uint)sound.Length));
        bank.Write("DATA"u8);
        WriteUInt32(bank, checked((uint)sound.Length));
        bank.Write(sound);
        return bank.ToArray();
    }

    /// <summary>
    /// An archive whose only texture clears the preview resource guard and then fails inside the
    /// texture helper, so a real worker records and forwards a decode failure.
    /// </summary>
    public static async Task<SyntheticArchiveFixture> CreateBrokenTextureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-broken-texture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = new SyntheticArchiveFixture(root);
        await BuildPackageAsync(root, "0001", [("texture/broken.dds", BuildUndecodableDds())]).ConfigureAwait(false);
        return fixture;
    }

    /// <summary>
    /// A well-formed 64x64 BC7 header carrying no pixel payload. The header reads cleanly, so the
    /// resource limits accept it, and DirectXTex then fails to load the surface.
    /// </summary>
    public static byte[] BuildUndecodableDds()
    {
        var bytes = new byte[148];
        "DDS "u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x00081007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 4);
        "DX10"u8.CopyTo(bytes.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108), 0x00001000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(128), 98); // DXGI_FORMAT_BC7_UNORM
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(132), 3);  // DDS_DIMENSION_TEXTURE2D
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(140), 1);
        return bytes;
    }

    public static async Task<SyntheticArchiveFixture> CreateNameIndexAsync()
    {
        const uint exactModelHash = 0x1D586E71;
        const uint relatedModelHash = 0xA1B2C3D4;
        const string localizationId = "12345678";
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-archive-lite-names-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var fixture = new SyntheticArchiveFixture(root);

        var itemInfo = BuildItemInfo(
            itemId: 1234,
            internalName: "Item_Marni_Laser_Helm",
            localizationId,
            exactModelHash,
            relatedModelHash);
        var stringInfo = BuildStringInfo("Icon_Prefab_cd_marni_laser_hel_0001", relatedModelHash);
        var localization = BuildLocalization(localizationId, "Synthetic Blade");

        await BuildPackageAsync(
            root,
            "0008",
            [
                ("gamecommon/item/iteminfo.pabgb", itemInfo),
                ("gamecommon/item/stringinfo.pabgb", stringInfo),
            ]).ConfigureAwait(false);
        await BuildPackageAsync(
            root,
            "0009",
            [
                ("character/model/cd_test_01_sword.pac", new byte[] { 0x50, 0x41, 0x43, 0x00 }),
                ("character/model/cd_marni_laser_hel_0001_index01.pac", new byte[] { 0x50, 0x41, 0x43, 0x01 }),
                ("ui/itemicon/itemicon_prefab_cd_marni_laser_hel_0001_n.dds", BuildDecodableDds()),
            ]).ConfigureAwait(false);
        await BuildPackageAsync(
            root,
            "0020",
            [("localization/localizationstring_eng.pabgb", localization)]).ConfigureAwait(false);
        return fixture;
    }

    public async Task AddSingleEntryPackageAsync(string packageName, string virtualPath, byte[] payload)
    {
        var packageDirectory = Path.Combine(Root, packageName);
        Directory.CreateDirectory(packageDirectory);
        var pamtPath = Path.Combine(packageDirectory, "0.pamt");
        var pazPath = Path.Combine(packageDirectory, "0.paz");
        await File.WriteAllBytesAsync(pazPath, payload).ConfigureAwait(false);
        await File.WriteAllBytesAsync(
            pamtPath,
            BuildPamt([new EntrySpec(virtualPath, 0, checked((uint)payload.Length), checked((uint)payload.Length), 0)]))
            .ConfigureAwait(false);
    }

    private static byte[] BuildSyntheticPac()
    {
        const int gridWidth = 4;
        const int vertexStride = 40;
        const int vertexCount = gridWidth * gridWidth;
        const int indexCount = (gridWidth - 1) * (gridWidth - 1) * 6;
        var section0 = new byte[64];
        section0[4] = 1;
        const int descriptor = 8;
        section0[descriptor] = 1;
        BinaryPrimitives.WriteSingleLittleEndian(section0.AsSpan(descriptor + 23, 4), 3.0f);
        BinaryPrimitives.WriteSingleLittleEndian(section0.AsSpan(descriptor + 27, 4), 3.0f);
        BinaryPrimitives.WriteSingleLittleEndian(section0.AsSpan(descriptor + 31, 4), 1.0f);
        section0[descriptor + 35] = 0x02;
        section0[descriptor + 36] = 0x00;
        section0[descriptor + 37] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(section0.AsSpan(descriptor + 40, 2), vertexCount);
        BinaryPrimitives.WriteUInt32LittleEndian(section0.AsSpan(descriptor + 44, 4), indexCount);

        var geometry = new byte[vertexCount * vertexStride + indexCount * sizeof(ushort)];
        var packedNormal = 1023u | (512u << 10) | (512u << 20);
        for (var y = 0; y < gridWidth; y++)
        {
            for (var x = 0; x < gridWidth; x++)
            {
                var vertex = (y * gridWidth + x) * vertexStride;
                BinaryPrimitives.WriteUInt16LittleEndian(
                    geometry.AsSpan(vertex, 2),
                    checked((ushort)Math.Round(x / 3.0 * 32767.0)));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    geometry.AsSpan(vertex + 2, 2),
                    checked((ushort)Math.Round(y / 3.0 * 32767.0)));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    geometry.AsSpan(vertex + 8, 2),
                    BitConverter.HalfToUInt16Bits((Half)(x / 3.0f)));
                BinaryPrimitives.WriteUInt16LittleEndian(
                    geometry.AsSpan(vertex + 10, 2),
                    BitConverter.HalfToUInt16Bits((Half)(y / 3.0f)));
                BinaryPrimitives.WriteUInt32LittleEndian(geometry.AsSpan(vertex + 16, 4), packedNormal);
            }
        }
        var indexOffset = vertexCount * vertexStride;
        var index = 0;
        for (var y = 0; y < gridWidth - 1; y++)
        {
            for (var x = 0; x < gridWidth - 1; x++)
            {
                var topLeft = checked((ushort)(y * gridWidth + x));
                var topRight = checked((ushort)(topLeft + 1));
                var bottomLeft = checked((ushort)(topLeft + gridWidth));
                var bottomRight = checked((ushort)(bottomLeft + 1));
                foreach (var value in new[] { topLeft, topRight, bottomLeft, topRight, bottomRight, bottomLeft })
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(
                        geometry.AsSpan(indexOffset + index * sizeof(ushort), sizeof(ushort)),
                        value);
                    index++;
                }
            }
        }

        var pac = new byte[0x50 + section0.Length + geometry.Length];
        "PAR "u8.CopyTo(pac);
        BinaryPrimitives.WriteUInt32LittleEndian(pac.AsSpan(0x14, 4), checked((uint)section0.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(pac.AsSpan(0x34, 4), checked((uint)geometry.Length));
        section0.CopyTo(pac, 0x50);
        geometry.CopyTo(pac, 0x50 + section0.Length);
        return pac;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Memory maps can release just after the test scope; temp cleanup is best-effort.
        }
        try
        {
            if (Directory.Exists(OutputRoot)) Directory.Delete(OutputRoot, recursive: true);
        }
        catch (IOException)
        {
            // Export handles can release just after the test scope.
        }
        return ValueTask.CompletedTask;
    }

    private static byte[] BuildPamt(IReadOnlyList<EntrySpec> entries)
    {
        using var output = new MemoryStream();
        WriteUInt32(output, 0);
        WriteUInt32(output, 1);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);
        WriteUInt32(output, 0);

        using var names = new MemoryStream();
        var nameOffsets = new List<uint>();
        foreach (var entry in entries)
        {
            nameOffsets.Add(checked((uint)names.Position));
            WriteUInt32(names, uint.MaxValue);
            var path = Encoding.UTF8.GetBytes(entry.Path);
            if (path.Length > byte.MaxValue) throw new InvalidOperationException("Synthetic path is too long.");
            names.WriteByte((byte)path.Length);
            names.Write(path);
        }
        WriteUInt32(output, checked((uint)names.Length));
        names.Position = 0;
        names.CopyTo(output);
        WriteUInt32(output, 0);
        WriteUInt32(output, checked((uint)entries.Count));
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            WriteUInt32(output, nameOffsets[index]);
            WriteUInt32(output, entry.Offset);
            WriteUInt32(output, entry.StoredSize);
            WriteUInt32(output, entry.OriginalSize);
            WriteUInt16(output, 0);
            WriteUInt16(output, entry.Flags);
        }
        return output.ToArray();
    }

    private static async Task BuildPackageAsync(
        string root,
        string package,
        IReadOnlyList<(string Path, byte[] Bytes)> payloads)
    {
        var packageRoot = Path.Combine(root, package);
        Directory.CreateDirectory(packageRoot);
        var pazPath = Path.Combine(packageRoot, "0.paz");
        var entries = new List<EntrySpec>(payloads.Count);
        uint offset = 0;
        await using (var stream = new FileStream(
            pazPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous))
        {
            foreach (var payload in payloads)
            {
                await stream.WriteAsync(payload.Bytes).ConfigureAwait(false);
                entries.Add(new EntrySpec(
                    payload.Path,
                    offset,
                    checked((uint)payload.Bytes.Length),
                    checked((uint)payload.Bytes.Length),
                    0));
                offset = checked(offset + (uint)payload.Bytes.Length);
            }
            await stream.FlushAsync().ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        await File.WriteAllBytesAsync(Path.Combine(packageRoot, "0.pamt"), BuildPamt(entries)).ConfigureAwait(false);
    }

    private static byte[] BuildItemInfo(
        uint itemId,
        string internalName,
        string localizationId,
        uint exactModelHash,
        uint relatedModelHash)
    {
        ReadOnlySpan<byte> marker =
        [
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x07, 0x70, 0x00, 0x00, 0x00,
        ];
        using var output = new MemoryStream();
        var name = Encoding.ASCII.GetBytes(internalName);
        WriteUInt32(output, itemId);
        WriteUInt32(output, checked((uint)name.Length));
        output.Write(name);
        output.Write(marker);
        output.Write(new byte[16]);
        var localization = Encoding.ASCII.GetBytes(localizationId);
        WriteUInt32(output, checked((uint)localization.Length));
        output.Write(localization);
        output.WriteByte(0x0E);
        output.WriteByte(0);
        output.WriteByte(0);
        WriteUInt32(output, 6);
        WriteUInt32(output, 6);
        WriteUInt32(output, 0x11111111);
        WriteUInt32(output, 0x22222222);
        WriteUInt32(output, 0x33333333);
        WriteUInt32(output, 0x44444444);
        WriteUInt32(output, 0x55555555);
        WriteUInt32(output, 0x66666666);
        output.WriteByte(0x0F);
        output.WriteByte(0);
        output.WriteByte(0);
        WriteUInt32(output, 1);
        WriteUInt32(output, 1);
        WriteUInt32(output, exactModelHash);
        WriteUInt32(output, relatedModelHash);
        output.Write(new byte[32]);
        return output.ToArray();
    }

    private static byte[] BuildStringInfo(string value, uint storedHash)
    {
        using var output = new MemoryStream();
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(output, checked((uint)bytes.Length));
        output.Write(bytes);
        WriteUInt32(output, storedHash);
        return output.ToArray();
    }

    private static byte[] BuildLocalization(string id, string value)
    {
        using var output = new MemoryStream();
        var idBytes = Encoding.ASCII.GetBytes(id);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        WriteUInt32(output, checked((uint)idBytes.Length));
        output.Write(idBytes);
        WriteUInt32(output, checked((uint)valueBytes.Length));
        output.Write(valueBytes);
        return output.ToArray();
    }

    private static byte[] EncodeLz4Literal(byte[] bytes)
    {
        using var output = new MemoryStream();
        if (bytes.Length < 15)
        {
            output.WriteByte((byte)(bytes.Length << 4));
        }
        else
        {
            output.WriteByte(0xF0);
            var remaining = bytes.Length - 15;
            while (remaining >= 255)
            {
                output.WriteByte(255);
                remaining -= 255;
            }
            output.WriteByte((byte)remaining);
        }
        output.Write(bytes);
        return output.ToArray();
    }

    /// <summary>A complete, decodable 4x4 DXT1 surface.</summary>
    public static byte[] BuildDecodableDds()
    {
        using var payload = new MemoryStream();
        payload.Write(BuildDdsHeader());
        payload.Write(Bc1Block);
        return payload.ToArray();
    }

    private static readonly byte[] Bc1Block = [1, 2, 3, 4, 5, 6, 7, 8];

    private static byte[] BuildDdsHeader()
    {
        var header = new byte[0x80];
        "DDS "u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 0x00081007);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(36), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(80), 4);
        "DXT1"u8.CopyTo(header.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(108), 0x00001000);
        return header;
    }

    private static (byte[] Payload, byte[] Pathc) BuildPartialDds()
    {
        var header = BuildDdsHeader();

        using var payload = new MemoryStream();
        payload.Write(header);
        payload.WriteByte(0x80);
        payload.Write(Bc1Block);

        using var pathc = new MemoryStream();
        WriteUInt32(pathc, 0);
        WriteUInt32(pathc, 0);
        WriteUInt32(pathc, 0x80);
        WriteUInt32(pathc, 1);
        WriteUInt32(pathc, 1);
        WriteUInt32(pathc, 0);
        WriteUInt32(pathc, 0);
        pathc.Write(header);
        WriteUInt32(pathc, 0x54E11B82);
        WriteUInt16(pathc, 0);
        pathc.WriteByte(0);
        pathc.WriteByte(0);
        pathc.Write(new byte[16]);
        return (payload.ToArray(), pathc.ToArray());
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private sealed record EntrySpec(string Path, uint Offset, uint StoredSize, uint OriginalSize, ushort Flags);
}
