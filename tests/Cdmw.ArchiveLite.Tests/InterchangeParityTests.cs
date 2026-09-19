using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cdmw.ArchiveLite.Contracts;
using Cdmw.ArchiveLite.Core;

namespace Cdmw.ArchiveLite.Tests;

internal static partial class ArchiveLiteTestRunner
{
    private static async Task TestMeshBundlePublicationAsync()
    {
        await using var fixture = await SyntheticArchiveFixture.CreateAssociatedAssetsAsync();
        var native = new NativeArchiveCore();
        using var sessions = new ArchiveSessionManager(native);
        var opened = await sessions.OpenAsync(new OpenArchiveRequest(fixture.Root, true), CancellationToken.None);
        var queries = new ArchiveQueryService(sessions);
        var mesh = (await queries.QueryAsync(new ArchiveQuerySpec(opened.SessionId, PathText: "hero.pac"), 20, CancellationToken.None))
            .Entries.Single(entry => entry.Extension == ".pac");
        var texture = (await queries.QueryAsync(new ArchiveQuerySpec(opened.SessionId, PathText: "hero_body_d.dds"), 20, CancellationToken.None)).Entries.Single();
        var service = new ArchiveExportService(sessions, queries, native, new NativeModelExportService(new NativeModelPreviewService()));
        var archiveBefore = await File.ReadAllBytesAsync(fixture.Paz);
        foreach (var kind in new[] { ExportKind.Obj, ExportKind.Fbx, ExportKind.Glb })
        {
            var directory = Path.Combine(fixture.OutputRoot, kind.ToString());
            var request = new ExportPlanRequest(opened.SessionId, kind, directory, [mesh.EntryId], null,
                CollisionPolicy: ExportCollisionPolicy.Overwrite, ManifestFormat: ExportManifestFormat.None,
                ReferencedEntryIds: [texture.EntryId]);
            var good = await service.ExportAsync(request, null, CancellationToken.None);
            Require(good.Exported == 1 && good.Failed == 0, $"{kind} synthetic archive bundle failed: {string.Join(';', good.Items.Select(item => item.Message))}");
            var oldFiles = Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
            var failed = await service.ExportAsync(request with { ReferencedEntryIds = [texture.EntryId, long.MaxValue] }, null, CancellationToken.None);
            Require(failed.Exported == 0 && failed.Failed == 1, $"{kind} reported success with a missing selected companion");
            foreach (var (path, bytes) in oldFiles) Require(File.ReadAllBytes(path).SequenceEqual(bytes), $"{kind} replaced an output before its whole bundle succeeded");
            Require(!Directory.EnumerateDirectories(directory, ".cdmw-*").Any(), "archive export left a staging bundle");
        }
        Require(File.ReadAllBytes(fixture.Paz).SequenceEqual(archiveBefore), "interchange export mutated the source archive");
    }

    private static async Task TestInterchangeParityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cdmw-lite-interchange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            // All eight distinct bones contribute. The stored weight total is 500, not 255.
            using (var geometry = new BinaryWriter(File.Create(Path.Combine(root, "vertices.bin"))))
            using (var exact = new BinaryWriter(File.Create(Path.Combine(root, "exact.bin"))))
            using (var identity = new BinaryWriter(File.Create(Path.Combine(root, "identity.bin"))))
            using (var skin = new BinaryWriter(File.Create(Path.Combine(root, "skin.bin"))))
            {
                for (var vertex = 0; vertex < 4; vertex++)
                {
                    var values = new double[] { vertex == 1 ? 1 : 0, vertex == 2 ? 1 : 0, 0, 0, 0, -1, 0, 0 };
                    foreach (var value in values) exact.Write(value);
                    if (vertex < 3)
                    {
                        for (var component = 0; component < 23; component++) geometry.Write(component < 8 ? (float)values[component] : 0f);
                        identity.Write(0); identity.Write(vertex);
                    }
                    for (ushort bone = 0; bone < 8; bone++) skin.Write(bone);
                    for (var bone = 0; bone < 8; bone++) skin.Write((byte)(bone < 6 ? 50 : 100));
                }
            }
            using (var bones = new BinaryWriter(File.Create(Path.Combine(root, "bones.bin"))))
            {
                for (var bone = 0; bone < 8; bone++)
                {
                    bones.Write(bone == 0 ? -1 : 0);
                    for (var matrix = 0; matrix < 2; matrix++)
                        for (var element = 0; element < 16; element++) bones.Write(element % 5 == 0 ? 1f : 0f);
                    foreach (var value in new[] { 1f, 1f, 1f, 0f, 0f, 0f, 1f, 0f, 0f, 0f }) bones.Write(value);
                }
            }
            var manifest = new
            {
                schema_version = 8,
                geometry_normalization = new { center = new[] { 0, 0, 0 }, scale = 1 },
                batches = new[] { new
                {
                    index = 0, material_name = "cloth", submesh_name = "part", submesh_texture = "body color.dds",
                    vertex_file = "vertices.bin", vertex_count = 3,
                    export_vertex_file = "exact.bin", export_vertex_count = 4, export_has_texture_coordinates = false,
                    skin_file = "skin.bin", skin_vertex_count = 4, skin_influences = 8,
                    editor_identity = new { identity_file = "identity.bin" },
                    base_color = new[] { 1f, 0f, 0.5f },
                } },
                skeleton = new { status = "rigged", source_path = "character.pab", bone_file = "bones.bin",
                    bone_names = new[] { "root", "one", "two", "three", "four", "five", "six", "일곱" } },
            };
            await File.WriteAllTextAsync(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest));
            var exporter = new NativeModelExportService(new NativeModelPreviewService());
            string[] textures = ["referenced_files/cloth_n.dds", "referenced_files/cloth_sp.dds", "referenced_files/textures/body color.dds"];
            foreach (var kind in new[] { ExportKind.Obj, ExportKind.Fbx, ExportKind.Glb })
                await exporter.ExportPackageAsync(root, "character.pac", kind,
                    Path.Combine(root, "mesh" + NativeModelExportService.FileExtension(kind)), false, null, null, textures, CancellationToken.None);

            var obj = await File.ReadAllTextAsync(Path.Combine(root, "mesh.obj"));
            Require(obj.Split('\n').Count(line => line.StartsWith("v ", StringComparison.Ordinal)) == 4, "OBJ dropped the unreferenced source vertex");
            Require(!obj.Contains("\nvt ", StringComparison.Ordinal), "OBJ invented missing UVs");
            var mtl = await File.ReadAllTextAsync(Path.Combine(root, "mesh.mtl"));
            Require(mtl.Contains("map_Kd referenced_files/textures/body color.dds", StringComparison.Ordinal), "OBJ chose a material prefix instead of the authored texture");
            Require(!mtl.Contains("cloth_n.dds", StringComparison.Ordinal), "OBJ bound a normal map as colour");
            using (var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "mesh.obj.meta.json"))))
                Require(metadata.RootElement.GetProperty("submeshes")[0].GetProperty("source_vertex_map").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual([0, 1, 2, 3]), "OBJ lost source correspondence");

            var fbx = await File.ReadAllBytesAsync(Path.Combine(root, "mesh.fbx"));
            var fbxText = Encoding.UTF8.GetString(fbx);
            Require(fbxText.Contains("referenced_files/textures/body color.dds", StringComparison.Ordinal) && fbxText.Contains("DiffuseColor", StringComparison.Ordinal), "FBX did not bind its companion texture");
            Require(fbxText.Contains("일곱", StringComparison.Ordinal), "FBX lost a Unicode bone name");
            RequireFbxUnitScale(fbx);
            await RequireNativeFbxShapeParityAsync(root);

            var glbBytes = await File.ReadAllBytesAsync(Path.Combine(root, "mesh.glb"));
            var jsonLength = BinaryPrimitives.ReadInt32LittleEndian(glbBytes.AsSpan(12));
            using var glbDocument = JsonDocument.Parse(glbBytes.AsMemory(20, jsonLength));
            var glb = glbDocument.RootElement;
            var attributes = glb.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes");
            Require(!attributes.TryGetProperty("TEXCOORD_0", out _), "GLB invented missing UVs");
            var weights = new List<float>();
            var joints = new List<ushort>();
            for (var set = 0; set < 2; set++)
            {
                int Offset(string name)
                {
                    var accessor = glb.GetProperty("accessors")[attributes.GetProperty(name).GetInt32()];
                    return 28 + jsonLength + glb.GetProperty("bufferViews")[accessor.GetProperty("bufferView").GetInt32()].GetProperty("byteOffset").GetInt32();
                }
                for (var lane = 0; lane < 4; lane++)
                {
                    weights.Add(BinaryPrimitives.ReadSingleLittleEndian(glbBytes.AsSpan(Offset($"WEIGHTS_{set}") + lane * 4)));
                    joints.Add(BinaryPrimitives.ReadUInt16LittleEndian(glbBytes.AsSpan(Offset($"JOINTS_{set}") + lane * 2)));
                }
            }
            Require(joints.Order().SequenceEqual(Enumerable.Range(0, 8).Select(x => (ushort)x)), "GLB dropped its seventh or eighth bone");
            Require(Math.Abs(weights.Sum() - 1f) < 1e-6 && weights.Count(x => Math.Abs(x - 0.2f) < 1e-6) == 2,
                "GLB did not normalize all eight stored influences together");
            var colour = glb.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorFactor");
            Require(colour[0].GetSingle() == colour[1].GetSingle() && colour[1].GetSingle() == colour[2].GetSingle(), "GLB exported the diagnostic preview palette as an authored tint");

            // A companion collision must not replace any part of the old export bundle.
            var before = new[] { "mesh.obj", "mesh.mtl", "mesh.obj.meta.json" }.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(root, name)));
            File.Delete(Path.Combine(root, "mesh.mtl"));
            Directory.CreateDirectory(Path.Combine(root, "mesh.mtl"));
            await RequireThrowsAsync<IOException>(() => exporter.ExportPackageAsync(root, "character.pac", ExportKind.Obj,
                Path.Combine(root, "mesh.obj"), true, null, null, textures, CancellationToken.None));
            Require(File.ReadAllBytes(Path.Combine(root, "mesh.obj")).SequenceEqual(before["mesh.obj"]), "failed companion publication replaced the OBJ");
            Require(File.ReadAllBytes(Path.Combine(root, "mesh.obj.meta.json")).SequenceEqual(before["mesh.obj.meta.json"]), "failed publication replaced the manifest");
            Require(!Directory.EnumerateDirectories(root, ".cdmw-*").Any(), "failed export leaked staging directories");

            // Allow the backup read but deny replacement of the second file, after the OBJ
            // has already moved. This exercises restoration, not only collision preflight.
            Directory.Delete(Path.Combine(root, "mesh.mtl"));
            await File.WriteAllBytesAsync(Path.Combine(root, "mesh.mtl"), before["mesh.mtl"]);
            using (var changed = new BinaryWriter(File.OpenWrite(Path.Combine(root, "exact.bin")))) changed.Write(9.25d);
            using (var lockedMaterial = File.Open(Path.Combine(root, "mesh.mtl"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var failed = false;
                try
                {
                    await exporter.ExportPackageAsync(root, "character.pac", ExportKind.Obj,
                        Path.Combine(root, "mesh.obj"), true, null, null, textures, CancellationToken.None);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }
                Require(failed, "export replaced a companion that was locked against replacement");
            }
            foreach (var (name, bytes) in before)
                Require(File.ReadAllBytes(Path.Combine(root, name)).SequenceEqual(bytes), $"rollback did not restore {name}");

            var skinPath = Path.Combine(root, "skin.bin");
            using (var skin = File.OpenWrite(skinPath)) skin.SetLength(skin.Length - 1);
            foreach (var kind in new[] { ExportKind.Obj, ExportKind.Fbx, ExportKind.Glb })
            {
                var path = Path.Combine(root, "mesh" + NativeModelExportService.FileExtension(kind));
                var previous = await File.ReadAllBytesAsync(path);
                await RequireThrowsAsync<InvalidDataException>(() => exporter.ExportPackageAsync(root, "character.pac", kind,
                    path, true, null, null, textures, CancellationToken.None));
                Require(File.ReadAllBytes(path).SequenceEqual(previous), $"{kind} replaced an export after silently discarding malformed skin data");
            }
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task RequireNativeFbxShapeParityAsync(string root)
    {
        var output = Path.Combine(root, "shape.fbx");
        var job = Path.Combine(root, "shape-job.json");
        var report = Path.Combine(root, "shape-report.json");
        var payload = new
        {
            version = 1, backend = "cdmw_mesh_core_0.1", operation = "fbx_export", output_path = output,
            base_name = "shape", scale = 1.0,
            submeshes = new[] { new
            {
                index = 0, name = "face", material = "skin",
                vertices = new[] { new[] { 0d, 0d, 0d }, new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0d } },
                faces = new[] { new[] { 0, 1, 2 } },
                morph_targets = new[] { new
                {
                    name = "표정",
                    vertices = new[] { new[] { 0d, 0d, 0d }, new[] { 1d, 0d, 0d }, new[] { 0d, 1d, 0.25d } },
                } },
            } },
        };
        await File.WriteAllTextAsync(job, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        var executable = Environment.GetEnvironmentVariable("CDMW_ARCHIVE_LITE_MESH_CORE_PATH")
            ?? Path.Combine(FindRepositoryRoot(), "native", "cdmw_mesh_core", "build", FindBuildConfiguration(), "cdmw-mesh-core.exe");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true,
            ArgumentList = { "fbx-export-json", job, report },
        }) ?? throw new InvalidOperationException("Native FBX shape test did not start.");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await stdout;
        Require(process.ExitCode == 0, $"Native FBX shape export failed: {await stderr}");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(report));
        Require(result.RootElement.GetProperty("morph_target_count").GetInt32() == 1, "Native FBX writer dropped a supplied morph target");
        var content = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(output));
        Require(content.Contains("BlendShapeChannel", StringComparison.Ordinal) && content.Contains("표정", StringComparison.Ordinal),
            "Native FBX shape export lost its channel or Unicode target name");
    }
}
