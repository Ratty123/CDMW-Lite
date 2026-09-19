using System.Drawing;
using System.Drawing.Imaging;

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
            && surface.GetPixel(0, 0).B == 255 && surface.GetPixel(0, 0).G == 0
            && surface.GetPixel(1, 0).G == 255 && surface.GetPixel(1, 0).B == 0
            && surface.GetPixel(1022, 511).B == 255 && surface.GetPixel(1023, 511).G == 255;

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
        using var small = NetMaterialLayerCompiler.Compile(baseColor,
            [new NetMaterialLayerSource(binding with { Weight = 0 }, large, mask)]);
        using var smallSurface = NetMaterialLayerCompiler.CompileSurface(seedSurface,
            [new NetMaterialLayerSurfaceSource(binding with { Weight = 0 }, large, mask)]);

        return new Dictionary<string, bool>
        {
            ["fine_selector_survives_small_base_swatch"] = maskDetail,
            ["base_cutout_alpha_survives_resampling"] = alphaPreserved,
            ["surface_detail_follows_selector_without_tint"] = surfaceDetail,
            ["fine_overlay_survives_small_base_swatch"] = overlayDetail,
            ["native_size_color_texels_are_preserved"] = sourceDetail,
            ["large_composites_are_bounded_and_keep_aspect"] = bounded,
            ["inactive_layers_do_not_inflate_small_materials"] = small is { Width: 4, Height: 4 }
                && smallSurface is { Width: 4, Height: 4 },
            ["source_orientation_is_preserved"] = NetMaterialLayerCompiler.PreservesSourceOrientation(),
            ["surface_layers_follow_mask"] = NetMaterialLayerCompiler.CompositesSurfaceThroughMask(),
        };
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
