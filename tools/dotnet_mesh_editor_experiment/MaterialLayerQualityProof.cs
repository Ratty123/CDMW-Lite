using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Cdmw.MeshEditorExperiment;

// CPU pixel checks used by the existing nonvisual material-policy probe.
internal static class MaterialLayerQualityProof
{
    public static Dictionary<string, bool> Run()
    {
        var binding = new NetMaterialLayerBinding("detail", "r", 1.0f, 1.0f, 1.0f, 1.0f, "layer", "mask");
        using var baseColor = Solid(4, 4, Color.FromArgb(173, 255, 0, 0));
        using var overlay = Solid(4, 4, Color.Blue);
        using var mask = Stripes(1024, 512, Color.White, Color.Black);
        using var maskedColor = NetMaterialLayerCompiler.Compile(baseColor,
            [new NetMaterialLayerSource(binding, overlay, mask)]);
        var maskDetail = maskedColor is { Width: 1024, Height: 512 }
            && maskedColor.GetPixel(0, 0).B == 255
            && maskedColor.GetPixel(1, 0).R >= 253
            && maskedColor.GetPixel(1022, 511).B == 255
            && maskedColor.GetPixel(1023, 511).R >= 253;
        var alphaPreserved = maskedColor is not null
            && maskedColor.GetPixel(0, 0).A == 173
            && maskedColor.GetPixel(maskedColor.Width - 1, maskedColor.Height - 1).A == 173;

        using var seedSurface = Solid(4, 4, Color.FromArgb(255, 0, 255, 0));
        using var layerSurface = Solid(4, 4, Color.FromArgb(255, 0, 0, 255));
        using var surface = NetMaterialLayerCompiler.CompileSurface(seedSurface,
            [new NetMaterialLayerSurfaceSource(binding with { TintB = 0 }, layerSurface, mask)]);
        var surfaceDetail = surface is { Width: 1024, Height: 512 }
            && surface.GetPixel(0, 0).B == 235 && surface.GetPixel(0, 0).G == 10
            && surface.GetPixel(1, 0).G == 255 && surface.GetPixel(1, 0).B == 0
            && surface.GetPixel(1022, 511).B == 235 && surface.GetPixel(1023, 511).G == 255;

        using var fineOverlay = Stripes(2048, 32, Color.Blue, Color.Green);
        using var overlayComposite = NetMaterialLayerCompiler.Compile(baseColor,
            [new NetMaterialLayerSource(binding, fineOverlay, null)]);
        var overlayDetail = overlayComposite is { Width: 2048, Height: 32 }
            && overlayComposite.GetPixel(0, 0).B == 255
            && overlayComposite.GetPixel(1, 0).G == 128
            && overlayComposite.GetPixel(2047, 31).G == 128;
        using var nativeComposite = NetMaterialLayerCompiler.Compile(fineOverlay, []);
        var sourceDetail = nativeComposite is { Width: 2048, Height: 32 }
            && nativeComposite.GetPixel(0, 0).ToArgb() == fineOverlay.GetPixel(0, 0).ToArgb()
            && nativeComposite.GetPixel(1, 0).ToArgb() == fineOverlay.GetPixel(1, 0).ToArgb()
            && nativeComposite.GetPixel(2047, 31).ToArgb() == fineOverlay.GetPixel(2047, 31).ToArgb();

        using var large = Solid(4096, 64, Color.Cyan);
        using var boundedColor = NetMaterialLayerCompiler.Compile(large, []);
        using var boundedSurface = NetMaterialLayerCompiler.CompileSurface(large, []);
        var bounded = boundedColor is { Width: 2048, Height: 32 }
            && boundedSurface is { Width: 2048, Height: 32 };
        using var thumbnailColor = NetMaterialLayerCompiler.Compile(large, [], maximumDimension: 256);
        using var thumbnailSurface = NetMaterialLayerCompiler.CompileSurface(large, [], maximumDimension: 256);
        using var thumbnailSupport = NetMaterialLayerCompiler.CompileSupport(large, [], normal: false, maximumDimension: 256);
        using var small = NetMaterialLayerCompiler.Compile(baseColor,
            [new NetMaterialLayerSource(binding with { Weight = 0 }, large, mask)]);
        using var smallSurface = NetMaterialLayerCompiler.CompileSurface(seedSurface,
            [new NetMaterialLayerSurfaceSource(binding with { Weight = 0 }, large, mask)]);

        using var neutral = Solid(4, 4, Color.FromArgb(173, 128, 128, 128));
        using var selector = Stripes(4, 4, Color.FromArgb(255, 255, 255, 0), Color.Black);
        var red = binding with { LayerRole = "color_seed", TintR = 1, TintG = 0, TintB = 0, DiffuseResourceId = "" };
        var blue = red with { MaskChannel = "g", TintR = 0, TintB = 1 };
        using var palette = NetMaterialLayerCompiler.Compile(neutral,
            [new(red, null, selector), new(blue, null, selector)]);
        using var reversedPalette = NetMaterialLayerCompiler.Compile(neutral,
            [new(blue, null, selector), new(red, null, selector)]);
        using var disabledPalette = NetMaterialLayerCompiler.Compile(neutral,
            [new(red with { TintA = 0 }, null, selector)]);
        using var untintedDetail = NetMaterialLayerCompiler.Compile(neutral,
            [new(binding with { TintR = 0.1f, TintG = 0.2f, TintB = 0.3f, TintA = 0 }, overlay, selector)]);
        using var grimedPalette = NetMaterialLayerCompiler.Compile(neutral,
            [new(red, null, selector), new(binding with { LayerRole = "grime" }, overlay, selector)]);
        using var flatNormal = Solid(4, 4, Color.FromArgb(128, 128, 255));
        using var slopedNormal = Solid(4, 4, Color.FromArgb(204, 128, 230));
        using var composedNormal = NetMaterialLayerCompiler.CompileSupport(flatNormal,
            [new(binding, slopedNormal, selector)], normal: true);
        using var composedHeight = NetMaterialLayerCompiler.CompileSupport(neutral,
            [new(binding, overlay, selector)], normal: false);
        using var skinSurface = NetMaterialLayerCompiler.CompileSurface(null,
            [new(binding with { ShaderRule = "skin" }, layerSurface, null)]);
        using var alphaData = Solid(2, 1, Color.Blue);
        alphaData.SetPixel(0, 0, Color.FromArgb(0, 255, 0, 0));
        using var largerMask = Solid(3, 1, Color.Black);
        using var independentlyScaled = NetMaterialLayerCompiler.Compile(alphaData, [new(binding, alphaData, largerMask)]);
        var parallel = ParallelMatchesSerialRows();

        return new Dictionary<string, bool>
        {
            ["fine_selector_survives_small_base_swatch"] = maskDetail,
            ["base_cutout_alpha_survives_resampling"] = alphaPreserved,
            ["surface_detail_follows_selector_without_tint"] = surfaceDetail,
            ["fine_overlay_survives_small_base_swatch"] = overlayDetail,
            ["native_size_color_texels_are_preserved"] = sourceDetail,
            ["large_composites_are_bounded_and_keep_aspect"] = bounded,
            ["thumbnail_size_does_not_reduce_interactive_detail"] = bounded
                && thumbnailColor is { Width: 256, Height: 4 }
                && thumbnailSurface is { Width: 256, Height: 4 }
                && thumbnailSupport is { Width: 256, Height: 4 }
                && thumbnailColor.GetPixel(128, 2).ToArgb() == large.GetPixel(2048, 32).ToArgb(),
            ["inactive_layers_do_not_inflate_small_materials"] = small is { Width: 4, Height: 4 }
                && smallSurface is { Width: 4, Height: 4 },
            ["source_orientation_is_preserved"] = NetMaterialLayerCompiler.PreservesSourceOrientation(),
            ["surface_layers_follow_mask"] = NetMaterialLayerCompiler.CompositesSurfaceThroughMask(),
            ["overlapping_palette_channels_blend_together"] = palette is not null
                && palette.GetPixel(0, 0) == Color.FromArgb(173, 128, 0, 128)
                && palette.GetPixel(1, 0) == neutral.GetPixel(1, 0),
            ["palette_order_does_not_change_color"] = palette is not null && reversedPalette is not null
                && palette.GetPixel(0, 0) == reversedPalette.GetPixel(0, 0),
            ["zero_palette_and_detail_strength_preserve_albedo"] = disabledPalette?.GetPixel(0, 0) == neutral.GetPixel(0, 0)
                && untintedDetail?.GetPixel(0, 0) == neutral.GetPixel(0, 0),
            ["grime_keeps_authored_dye_hue"] = grimedPalette is not null
                && grimedPalette.GetPixel(0, 0).R > 210 && grimedPalette.GetPixel(0, 0).B == 0,
            ["normal_and_height_follow_selector"] = composedNormal is not null && composedHeight is not null
                && composedNormal.GetPixel(0, 0).R > 195 && composedNormal.GetPixel(1, 0).R == 128
                && composedHeight.GetPixel(0, 0).R == 0 && composedHeight.GetPixel(1, 0).R == 128,
            ["skin_packed_response_does_not_become_metal"] = skinSurface is not null
                && skinSurface.GetPixel(0, 0).B == 0 && skinSurface.GetPixel(0, 0).A is >= 75 and <= 77,
            ["technical_dds_stays_linear_despite_srgb_header"] = !D3D11MaterialViewport.TextureUsesSrgb("linear", true)
                && D3D11MaterialViewport.TextureUsesSrgb("srgb", false)
                && D3D11MaterialViewport.TextureUsesSrgb("auto", true),
            ["resampling_does_not_premultiply_dds_channels"] = independentlyScaled?.GetPixel(1, 0)
                == Color.FromArgb(128, 128, 0, 128),
            ["parallel_composites_match_serial_rows"] = parallel.PixelsMatch,
            ["shared_layer_inputs_remain_unchanged"] = parallel.InputsUnchanged,
        };
    }

    private static (bool PixelsMatch, bool InputsUnchanged) ParallelMatchesSerialRows()
    {
        // One row takes the serial path; the tall image exercises parallel row
        // boundaries, including a final partial range, with identical inputs.
        var serial = CompileRepeatedRows(1);
        var parallel = CompileRepeatedRows(259);
        var equal = serial.Maps.All(pair =>
        {
            var pixels = parallel.Maps[pair.Key];
            if (pixels.Length != pair.Value.Length * 259) return false;
            for (var row = 0; row < 259; row++)
                if (!pixels.AsSpan(row * pair.Value.Length, pair.Value.Length).SequenceEqual(pair.Value)) return false;
            return true;
        });
        return (equal, serial.InputsUnchanged && parallel.InputsUnchanged);
    }

    private static (Dictionary<string, byte[]> Maps, bool InputsUnchanged) CompileRepeatedRows(int height)
    {
        using var source = Stripes(257, height, Color.FromArgb(173, 120, 90, 70), Color.FromArgb(0, 50, 80, 100));
        using var mask = Stripes(257, height, Color.FromArgb(191, 255, 127, 63), Color.FromArgb(37, 0, 63, 127));
        // Repeated sources and masks exercise reuse; distinct swatches exceed
        // the bounded input cache. Scaling must preserve all four DDS channels.
        var swatches = Enumerable.Range(0, 6).Select(i => Stripes(3 + i, 1,
            Color.FromArgb(0, 35 + 21 * i, 90, 180), Color.FromArgb(203, 210, 50 + 17 * i, 70))).ToArray();
        try
        {
            var originals = new[] { source, mask }.Concat(swatches).Select(Pixels).ToArray();
            var bindings = Enumerable.Range(0, 8).Select(i => new NetMaterialLayerBinding(
                "detail", new[] { "r", "g", "b", "a" }[i % 4], 0.6f, 0.7f, 0.9f, 0.5f, "", "",
                TintA: 0.8f, RoughnessHint: 0.3f, MetalnessHint: 0.4f, SpecularHint: 0.2f)).ToArray();
            var colors = bindings.Select((binding, i) => new NetMaterialLayerSource(
                binding, i < 2 ? source : swatches[i - 2], mask)).ToList();
            colors.Insert(0, new NetMaterialLayerSource(bindings[0] with { LayerRole = "color_seed" }, null, mask));
            colors.Insert(1, new NetMaterialLayerSource(bindings[1] with { LayerRole = "color_seed" }, null, mask));
            var support = bindings.Select((binding, i) => new NetMaterialLayerSurfaceSource(
                binding, i < 2 ? source : swatches[i - 2], mask)).ToArray();
            using var diffuse = NetMaterialLayerCompiler.Compile(source, colors);
            using var surface = NetMaterialLayerCompiler.CompileSurface(source, support);
            using var normal = NetMaterialLayerCompiler.CompileSupport(source, support, normal: true);
            using var displacement = NetMaterialLayerCompiler.CompileSupport(source, support, normal: false);
            var unchanged = new[] { source, mask }.Concat(swatches)
                .Select((bitmap, i) => Pixels(bitmap).AsSpan().SequenceEqual(originals[i])).All(value => value);
            return (new Dictionary<string, byte[]>
            {
                ["diffuse"] = Pixels(diffuse!), ["surface"] = Pixels(surface!),
                ["normal"] = Pixels(normal!), ["height"] = Pixels(displacement!),
            }, unchanged);
        }
        finally
        {
            foreach (var bitmap in swatches) bitmap.Dispose();
        }
    }

    private static byte[] Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = bitmap.Width * 4;
            var pixels = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * rowBytes, rowBytes);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static Bitmap Solid(int width, int height, Color color)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(color);
        return bitmap;
    }

    private static Bitmap Stripes(int width, int height, Color even, Color odd)
    {
        var bitmap = Solid(width, height, odd);
        using var graphics = Graphics.FromImage(bitmap);
        using var brush = new SolidBrush(even);
        for (var x = 0; x < width; x += 2) graphics.FillRectangle(brush, x, 0, 1, height);
        return bitmap;
    }
}
