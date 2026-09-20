using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace Cdmw.MeshEditorExperiment;

/// <summary>Uses one hidden D3D11 device for a Finder queue, publishing every completed image.</summary>
internal static class CharacterThumbnailBatch
{
    public static int Run(string path)
    {
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Thumbnail job exceeds 64 KiB.");
        var jobs = JsonSerializer.Deserialize<Job[]>(File.ReadAllText(path)) ?? [];
        if (jobs.Length is < 1 or > 4) throw new InvalidDataException("Thumbnail job must contain one to four packages.");
        using var renderer = new Renderer();
        foreach (var job in jobs) Publish(renderer, job);
        return 0;
    }

    public static int RunWorker()
    {
        using var renderer = new Renderer();
        while (Console.ReadLine() is { } line)
        {
            if (line.Length > 64 * 1024) throw new InvalidDataException("Thumbnail job exceeds 64 KiB.");
            var job = JsonSerializer.Deserialize<Job>(line) ?? throw new InvalidDataException("Missing thumbnail job.");
            Publish(renderer, job);
        }
        return 0;
    }

    private static void Publish(Renderer renderer, Job job)
    {
        try { Console.WriteLine(JsonSerializer.Serialize(renderer.Render(job))); }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { job.Key, Error = error.Message }));
        }
        Console.Out.Flush();
    }

    private sealed record Job(string Key, string Package, string Output);

    private sealed class Renderer : IDisposable
    {
        private const int Size = 160;
        private readonly Form _host = new()
        {
            ClientSize = new Size(Size, Size), ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
        };
        private D3D11MaterialViewport? _viewport;
        private NetTextureSet? _textures;

        public object Render(Job job)
        {
            var package = Path.GetFullPath(job.Package);
            var output = Path.GetFullPath(job.Output);
            if (!string.Equals(Path.GetDirectoryName(output), package, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(output) != "character-thumbnail-v1.png")
                throw new InvalidDataException("Thumbnail output must remain in its preview package.");
            var phase = Stopwatch.StartNew();
            var document = ObjDocument.Load(Path.Combine(package, "manifest.json"));
            var materials = NetMaterialSet.Load(Path.Combine(package, "net_materials.json"));
            var scene = NetSceneState.Load(Path.Combine(package, "dotnet_scene.json"), document.Submeshes.Count);
            var parseMs = phase.Elapsed.TotalMilliseconds;
            var textures = NetTextureSet.LoadForThumbnail();
            try
            {
                phase.Restart();
                textures.LoadAsync(materials).GetAwaiter().GetResult();
                var textureMs = phase.Elapsed.TotalMilliseconds;
                phase.Restart();
                if (_viewport is null)
                {
                    var viewport = new D3D11MaterialViewport(document, materials, textures, scene)
                        { Dock = DockStyle.Fill, ShowSolid = true, TexturesEnabled = true };
                    try
                    {
                        _host.Controls.Add(viewport);
                        _host.CreateControl();
                        _ = _host.Handle;
                        viewport.CreateControl();
                        _ = viewport.Handle;
                        if (!viewport.TryInitialize(out var failure)) throw new InvalidOperationException(failure);
                        _viewport = viewport;
                    }
                    catch { _host.Controls.Remove(viewport); viewport.Dispose(); throw; }
                }
                else _viewport.ReplaceResidentScene(document, materials, textures, scene);
                _textures?.Dispose();
                _textures = textures;
                var bounds = document.Bounds();
                var center = new Vec3((bounds.Min.X + bounds.Max.X) * 0.5f,
                    (bounds.Min.Y + bounds.Max.Y) * 0.5f, (bounds.Min.Z + bounds.Max.Z) * 0.5f);
                _viewport.UpdateCamera(NetViewportCamera.Create(center, bounds, 0.15f, 0.05f, 48f, 0f, 0f, Size, Size));
                var temporary = output + "." + Guid.NewGuid().ToString("N") + ".png";
                try
                {
                    if (!_viewport.TryCaptureReplacementPng(temporary, Size, Size, out _, out var failure))
                        throw new InvalidOperationException(failure);
                    File.Move(temporary, output, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return new { job.Key, Error = (string?)null, ProcessId = Environment.ProcessId,
                    SceneCount = _viewport.ResidentSceneLoadCount, parseMs, textureMs, renderMs = phase.Elapsed.TotalMilliseconds };
            }
            catch { if (!ReferenceEquals(_textures, textures)) textures.Dispose(); throw; }
        }

        public void Dispose()
        {
            _host.Dispose();
            _textures?.Dispose();
        }
    }
}
