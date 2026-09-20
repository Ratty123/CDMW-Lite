using System.Drawing;
using System.Drawing.Imaging;
using System.Numerics;

namespace Cdmw.MeshEditorExperiment;

internal static partial class NetMaterialLayerCompiler
{
    public static Bitmap? Compile(Bitmap? baseBitmap, IReadOnlyList<NetMaterialLayerSource> layers,
        Color? baseColor = null, int maximumDimension = 2048)
    {
        var source = baseBitmap ?? layers.FirstOrDefault(layer => layer.Binding.LayerRole == "base")?.Diffuse;
        var domain = source ?? layers.SelectMany(layer => new[] { layer.Diffuse, layer.Mask })
            .FirstOrDefault(bitmap => bitmap is not null);
        if (domain is null) return null;
        var (width, height) = CompositeSize(domain, layers.Where(layer => IsActiveLayer(layer.Binding))
            .SelectMany(layer => new[] { layer.Diffuse, layer.Mask }), maximumDimension);
        var target = source is null ? SolidBitmap(width, height, baseColor ?? Color.FromArgb(158, 158, 158))
            : new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var pixels = source is null ? ReadBgra(target) : ReadScaledBgra(source, width, height);
        var inputs = new LayerPixelCache(width, height);
        var seeded = ApplyColorSeeds(pixels, width, height, layers, inputs);
        foreach (var layer in layers)
        {
            var binding = layer.Binding;
            if (!IsActiveLayer(binding) || binding.LayerRole == "color_seed" || layer.Diffuse is null) continue;
            var overlay = inputs.Read(layer.Diffuse);
            var selectors = layer.Mask is null ? null : inputs.Read(layer.Mask);
            var channel = LayerChannelOffset(binding.MaskChannel);
            var tinted = Math.Abs(binding.TintR - 1) > 1f / 255
                || Math.Abs(binding.TintG - 1) > 1f / 255 || Math.Abs(binding.TintB - 1) > 1f / 255;
            var tint = new Vector3(binding.TintB, binding.TintG, binding.TintR);
            ForRows(width, height, (first, last) =>
            {
                for (var offset = first * width * 4; offset < last * width * 4; offset += 4)
                {
                    var coverage = Coverage(binding, selectors, offset, channel);
                    if (coverage <= 0) continue;
                    var background = ReadColor(pixels, offset);
                    var foreground = ReadColor(overlay, offset);
                    var modulation = 0.82f + 0.36f * Vector3.Dot(foreground, new Vector3(0.114f, 0.587f, 0.299f));
                    Vector3 output;
                    if (binding.LayerRole == "detail" && tinted)
                        output = Vector3.Lerp(background, Vector3.Clamp(tint * modulation, Vector3.Zero, Vector3.One),
                            coverage * Math.Clamp(binding.TintA, 0, 1));
                    else if (seeded && binding.LayerRole is "grime" or "layer" or "damage")
                        output = background * (1 - coverage + modulation * coverage);
                    else
                        output = Vector3.Lerp(background, Vector3.Clamp(tinted ? foreground * tint : foreground,
                            Vector3.Zero, Vector3.One), coverage);
                    WriteColor(pixels, offset, output);
                }
            });
        }
        WriteBgra(target, pixels);
        return target;
    }

    // R/G/B dye channels form one weighted palette, not three sequential alpha
    // overlays. Normalize the source luminance within each selected region so
    // the authored dye retains the DDS's shading and wear without changing hue.
    private static bool ApplyColorSeeds(byte[] pixels, int width, int height,
        IReadOnlyList<NetMaterialLayerSource> layers, LayerPixelCache inputs)
    {
        var seeds = new NetMaterialLayerSource?[3];
        foreach (var layer in layers.Where(layer => layer.Binding.LayerRole == "color_seed"))
        {
            var channel = layer.Binding.MaskChannel switch { "r" => 0, "g" => 1, "b" => 2, _ => -1 };
            if (channel >= 0) seeds[channel] ??= layer;
        }
        if (!seeds.Any(seed => seed?.Binding.TintA > 1f / 255)) return false;
        var masks = new byte[]?[3];
        var strengths = new float[3];
        var palette = new Vector3[3];
        for (var i = 0; i < 3; i++)
        {
            var seed = seeds[i];
            if (seed?.Mask is null) continue;
            masks[i] = inputs.Read(seed.Mask);
            strengths[i] = Math.Clamp(seed.Binding.TintA, 0, 1);
            palette[i] = new Vector3(seed.Binding.TintB, seed.Binding.TintG, seed.Binding.TintR);
        }
        var sums = new double[3];
        var weights = new double[3];
        double visibleLuma = 0;
        var visibleCount = 0;
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var luma = Vector3.Dot(ReadColor(pixels, offset), new Vector3(0.0722f, 0.7152f, 0.2126f));
            if (luma <= 1f / 255) continue;
            visibleLuma += luma;
            visibleCount++;
            for (var i = 0; i < 3; i++)
            {
                var weight = masks[i] is { } mask ? mask[offset + 2 - i] / 255f * strengths[i] : 0;
                sums[i] += luma * weight;
                weights[i] += weight;
            }
        }
        var references = new float[3];
        for (var i = 0; i < 3; i++)
        {
            var mean = weights[i] > 0.001 ? sums[i] / weights[i] : visibleCount > 0 ? visibleLuma / visibleCount : 0.5;
            references[i] = Math.Clamp((float)Math.Round(mean * 4096) / 4096, 1f / 255, 1);
        }
        ForRows(width, height, (first, last) =>
        {
            for (var offset = first * width * 4; offset < last * width * 4; offset += 4)
            {
                var seeded = Vector3.Zero;
                float sum = 0, reference = 0;
                for (var i = 0; i < 3; i++)
                {
                    var weight = masks[i] is { } mask ? mask[offset + 2 - i] / 255f * strengths[i] : 0;
                    sum += weight;
                    reference += references[i] * weight;
                    seeded += palette[i] * weight;
                }
                if (sum <= 0) continue;
                var background = ReadColor(pixels, offset);
                var luma = Vector3.Dot(background, new Vector3(0.0722f, 0.7152f, 0.2126f));
                var scale = Math.Clamp(luma / Math.Max(reference / sum, 1f / 255), 0.55f, 1.25f);
                WriteColor(pixels, offset, Vector3.Lerp(background,
                    Vector3.Clamp(seeded / sum * scale, Vector3.Zero, Vector3.One), Math.Clamp(sum, 0, 1)));
            }
        });
        return true;
    }

    // Output is linear RGBA: coverage=1, roughness, metalness, specular. Decode
    // each layer before blending; selector masks are never surface-response DDSs.
    public static Bitmap? CompileSurface(Bitmap? baseMaterial, IReadOnlyList<NetMaterialLayerSurfaceSource> layers, int maximumDimension = 2048)
    {
        var source = baseMaterial ?? layers.FirstOrDefault()?.Material;
        if (source is null) return null;
        var (width, height) = CompositeSize(source, layers.Where(layer => IsActiveLayer(layer.Binding)
            || layer.Binding.LayerRole == "base").SelectMany(layer => new[] { layer.Material, layer.Mask }), maximumDimension);
        var target = SolidBitmap(width, height, Color.FromArgb(10, 255, 148, 0));
        var pixels = ReadBgra(target);
        var sampled = new LayerPixelCache(width, height);
        var inputs = layers.ToList();
        if (baseMaterial is not null && !layers.Any(layer => layer.Binding.LayerRole == "base"))
            inputs.Insert(0, new NetMaterialLayerSurfaceSource(
                new NetMaterialLayerBinding("base", "r", 1, 1, 1, 1, "", ""), baseMaterial, null));
        foreach (var layer in inputs)
        {
            var overlay = sampled.Read(layer.Material);
            var selectors = layer.Mask is null ? null : sampled.Read(layer.Mask);
            var binding = layer.Binding;
            var channel = LayerChannelOffset(binding.MaskChannel);
            var family = new string(binding.ShaderFamily.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            var skin = binding.ShaderRule == "skin" || family.Contains("skinnedmeshskin") || family.Contains("skinwrinkle");
            var hair = binding.ShaderRule == "hair" || family.Contains("hair") || family.Contains("fur");
            ForRows(width, height, (first, last) =>
            {
                for (var offset = first * width * 4; offset < last * width * 4; offset += 4)
                {
                    var coverage = Coverage(binding, selectors, offset, channel);
                    if (coverage <= 0) continue;
                    var blue = overlay[offset] / 255f;
                    var roughness = Math.Clamp(overlay[offset + 1] / 255f, 0.04f, 1);
                    var metal = skin || hair ? 0 : Math.Clamp(Math.Max(blue - 0.18f, 0) * 1.22f, 0, 0.92f);
                    var specular = skin ? Math.Clamp(0.06f + 0.24f * blue, 0.04f, 0.34f)
                        : hair ? 0.19f : Math.Clamp(0.04f + 0.84f * blue, 0.04f, 0.88f);
                    if (binding.RoughnessHint > 0.02f)
                        roughness = Math.Clamp(roughness * 0.72f + binding.RoughnessHint * 0.28f, 0.04f, 0.98f);
                    if (binding.MetalnessHint > 0.02f)
                    {
                        metal = Math.Clamp(Math.Max(metal, binding.MetalnessHint * 0.42f), 0, 1);
                        specular = Math.Max(specular, 0.14f + binding.MetalnessHint * 0.32f);
                    }
                    if (binding.SpecularHint > 0.02f) specular = Math.Max(specular, binding.SpecularHint * 0.58f);
                    pixels[offset] = Blend(pixels[offset], metal * 255, coverage);
                    pixels[offset + 1] = Blend(pixels[offset + 1], roughness * 255, coverage);
                    pixels[offset + 2] = 255;
                    pixels[offset + 3] = Blend(pixels[offset + 3], specular * 255, coverage);
                }
            });
        }
        WriteBgra(target, pixels);
        return target;
    }

    public static Bitmap? CompileSupport(Bitmap? baseMap, IReadOnlyList<NetMaterialLayerSurfaceSource> layers, bool normal, int maximumDimension = 2048)
    {
        var domain = baseMap ?? layers.FirstOrDefault()?.Material;
        if (domain is null) return null;
        var (width, height) = CompositeSize(domain, layers.Where(layer => IsActiveLayer(layer.Binding))
            .SelectMany(layer => new[] { layer.Material, layer.Mask }), maximumDimension);
        var target = baseMap is null
            ? SolidBitmap(width, height, normal ? Color.FromArgb(128, 128, 255) : Color.FromArgb(128, 128, 128))
            : new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var pixels = baseMap is null ? ReadBgra(target) : ReadScaledBgra(baseMap, width, height);
        var inputs = new LayerPixelCache(width, height);
        ForRows(width, height, (first, last) =>
        {
            for (var offset = first * width * 4; offset < last * width * 4; offset += 4)
            {
                if (normal) PackNormal(pixels, offset, UnpackNormal(pixels, offset));
                else pixels[offset] = pixels[offset + 1] = pixels[offset + 2];
                pixels[offset + 3] = 255;
            }
        });
        foreach (var layer in layers.Where(layer => IsActiveLayer(layer.Binding)))
        {
            var overlay = inputs.Read(layer.Material);
            var selectors = layer.Mask is null ? null : inputs.Read(layer.Mask);
            var channel = LayerChannelOffset(layer.Binding.MaskChannel);
            ForRows(width, height, (first, last) =>
            {
                for (var offset = first * width * 4; offset < last * width * 4; offset += 4)
                {
                    var coverage = Coverage(layer.Binding, selectors, offset, channel);
                    if (coverage <= 0) continue;
                    if (normal)
                    {
                        var background = UnpackNormal(pixels, offset);
                        var foreground = UnpackNormal(overlay, offset);
                        var whiteout = Normalize(new Vector3(background.X + foreground.X, background.Y + foreground.Y,
                            background.Z * foreground.Z));
                        PackNormal(pixels, offset, Normalize(Vector3.Lerp(background, whiteout, coverage)));
                    }
                    else
                        pixels[offset] = pixels[offset + 1] = pixels[offset + 2]
                            = Blend(pixels[offset + 2], overlay[offset + 2], coverage);
                }
            });
        }
        WriteBgra(target, pixels);
        return target;
    }

    private static float Coverage(NetMaterialLayerBinding binding, byte[]? mask, int offset, int channel) =>
        Math.Clamp(binding.Weight * (mask is null ? 1 : mask[offset + channel] / 255f), 0, 1);

    private static Bitmap SolidBitmap(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static Vector3 ReadColor(byte[] pixels, int offset) =>
        new(pixels[offset] / 255f, pixels[offset + 1] / 255f, pixels[offset + 2] / 255f);

    private static void WriteColor(byte[] pixels, int offset, Vector3 color)
    {
        pixels[offset] = UnitByte(color.X);
        pixels[offset + 1] = UnitByte(color.Y);
        pixels[offset + 2] = UnitByte(color.Z);
    }

    private static byte UnitByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255,
        MidpointRounding.AwayFromZero), 0, 255);

    private static Vector3 UnpackNormal(byte[] pixels, int offset)
    {
        var x = pixels[offset + 2] / 255f * 2 - 1;
        var y = pixels[offset + 1] / 255f * 2 - 1;
        return Normalize(new Vector3(x, y, MathF.Sqrt(Math.Max(0, 1 - x * x - y * y))));
    }

    private static Vector3 Normalize(Vector3 value) => value.LengthSquared() < 1e-12f ? Vector3.UnitZ : Vector3.Normalize(value);

    private static void PackNormal(byte[] pixels, int offset, Vector3 normal) =>
        WriteColor(pixels, offset, new Vector3(normal.Z, normal.Y, normal.X) * 0.5f + new Vector3(0.5f));
}
