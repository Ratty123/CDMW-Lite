using System.Drawing;
using System.IO;
using System.Text.Json;

namespace Cdmw.MeshEditorExperiment;

/// <summary>Renders a bounded Finder batch in a hidden helper process using the regular material renderer.</summary>
internal static class CharacterThumbnailBatch
{
    public static int Run(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Thumbnail job exceeds 64 KiB.");
        var jobs = JsonSerializer.Deserialize<Job[]>(File.ReadAllText(path)) ?? [];
        if (jobs.Length is < 1 or > 4) throw new InvalidDataException("Thumbnail job must contain one to four packages.");
        foreach (var job in jobs)
        {
            var root = Path.GetFullPath(job.Package);
            var output = Path.GetFullPath(job.Output);
            if (!string.Equals(Path.GetDirectoryName(output), root, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(output) != "character-thumbnail-v1.png")
                throw new InvalidDataException("Thumbnail output must remain in its preview package.");
            try { Render(root, output); }
            catch (Exception error) { Console.Error.WriteLine($"{job.Key}: {error.Message}"); }
        }
        return 0;
    }

    private static void Render(string package, string output)
    {
        const int size = 160;
        var document = ObjDocument.Load(Path.Combine(package, "manifest.json"));
        var materials = NetMaterialSet.Load(Path.Combine(package, "net_materials.json"));
        using var textures = NetTextureSet.Load(materials);
        textures.LoadAsync(materials).GetAwaiter().GetResult();
        using var host = new Form
        {
            ClientSize = new Size(size, size), ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
        };
        using var viewport = new D3D11MaterialViewport(document, materials, textures,
            NetSceneState.Load(Path.Combine(package, "dotnet_scene.json"), document.Submeshes.Count))
        { Dock = DockStyle.Fill, ShowSolid = true, TexturesEnabled = true };
        host.Controls.Add(viewport);
        host.CreateControl();
        _ = host.Handle;
        viewport.CreateControl();
        _ = viewport.Handle;
        if (!viewport.TryInitialize(out var error)) throw new InvalidOperationException(error);
        var bounds = document.Bounds();
        var center = new Vec3((bounds.Min.X + bounds.Max.X) * 0.5f,
            (bounds.Min.Y + bounds.Max.Y) * 0.5f, (bounds.Min.Z + bounds.Max.Z) * 0.5f);
        viewport.UpdateCamera(NetViewportCamera.Create(center, bounds, 0.15f, 0.05f, 48f, 0f, 0f, size, size));
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".png";
        try
        {
            if (!viewport.TryCaptureReplacementPng(temporary, size, size, out _, out error))
                throw new InvalidOperationException(error);
            File.Move(temporary, output, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record Job(string Key, string Package, string Output);
}
