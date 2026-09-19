using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Cdmw.MeshEditorExperiment;

internal sealed record NetMaterialLayerSource(
    NetMaterialLayerBinding Binding,
    Bitmap? Diffuse,
    Bitmap? Mask);

internal sealed record NetMaterialLayerSurfaceSource(
    NetMaterialLayerBinding Binding,
    Bitmap Material,
    Bitmap? Mask);

internal static partial class NetMaterialLayerCompiler
{
    private const int MaximumDimension = 2048;

    private static bool IsActiveLayer(NetMaterialLayerBinding binding) =>
        !string.Equals(binding.LayerRole, "base", StringComparison.OrdinalIgnoreCase)
        && binding.Weight > 0.001f;

    private static (int Width, int Height) CompositeSize(Bitmap source, IEnumerable<Bitmap?> inputs)
    {
        // A small base swatch must not collapse a detailed selector or overlay.
        // Choose a real source domain, as Full does, then bound its size without
        // inflating small materials or changing the selected aspect ratio.
        var largest = source;
        foreach (var input in inputs)
        {
            if (input is null) continue;
            var area = (long)input.Width * input.Height;
            var largestArea = (long)largest.Width * largest.Height;
            if (area > largestArea || area == largestArea
                && Math.Max(input.Width, input.Height) > Math.Max(largest.Width, largest.Height))
            {
                largest = input;
            }
        }
        var scale = Math.Min(1.0, MaximumDimension / (double)Math.Max(largest.Width, largest.Height));
        return (Math.Max(1, (int)Math.Round(largest.Width * scale)),
            Math.Max(1, (int)Math.Round(largest.Height * scale)));
    }

    public static bool PreservesSourceOrientation()
    {
        using var baseBitmap = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var overlay = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var mask = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using (var baseGraphics = Graphics.FromImage(baseBitmap)) baseGraphics.Clear(Color.Blue);
        using (var overlayGraphics = Graphics.FromImage(overlay)) overlayGraphics.Clear(Color.Red);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                mask.SetPixel(x, y, y < 2 ? Color.White : Color.Black);
            }
        }
        var binding = new NetMaterialLayerBinding("detail", "r", 1.0f, 1.0f, 1.0f, 1.0f, "overlay", "mask");
        using var compiled = Compile(baseBitmap, new[] { new NetMaterialLayerSource(binding, overlay, mask) });
        return compiled is not null
            && compiled.GetPixel(1, 0).R > 220
            && compiled.GetPixel(1, 0).B < 35
            && compiled.GetPixel(1, 3).B > 220
            && compiled.GetPixel(1, 3).R < 35;
    }

    // Surface layers must follow the mask that selects their colour, and must not
    // take the layer tint: a tint recolours albedo, and folding it into roughness
    // or metal would invent surface properties the source never authored.
    public static bool CompositesSurfaceThroughMask()
    {
        using var seed = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var overlay = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        using var mask = new Bitmap(4, 4, PixelFormat.Format32bppArgb);
        // Seed reads roughness 1.0 / metal 0.0; the overlay reads the opposite.
        using (var seedGraphics = Graphics.FromImage(seed)) seedGraphics.Clear(Color.FromArgb(255, 0, 255, 0));
        using (var overlayGraphics = Graphics.FromImage(overlay)) overlayGraphics.Clear(Color.FromArgb(255, 0, 0, 255));
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                mask.SetPixel(x, y, y < 2 ? Color.White : Color.Black);
            }
        }
        var seedBinding = new NetMaterialLayerBinding("base", "r", 1.0f, 1.0f, 1.0f, 1.0f, "", "", "seed");
        // A saturated tint that must leave no trace in the composite.
        var overlayBinding = new NetMaterialLayerBinding("detail", "r", 1.0f, 0.0f, 0.0f, 2.0f, "", "mask", "overlay");
        using var compiled = CompileSurface(
            seed,
            new[]
            {
                new NetMaterialLayerSurfaceSource(seedBinding, seed, null),
                new NetMaterialLayerSurfaceSource(overlayBinding, overlay, mask),
            });
        if (compiled is null)
        {
            return false;
        }
        var covered = compiled.GetPixel(1, 0);
        var uncovered = compiled.GetPixel(1, 3);
        return covered.B > 220 && covered.G < 35
            && uncovered.G > 220 && uncovered.B < 35;
    }

    private static byte Blend(byte background, float foreground, float alpha)
    {
        return (byte)Math.Clamp(
            (int)Math.Round((background * (1.0f - alpha)) + (Math.Clamp(foreground, 0.0f, 255.0f) * alpha)),
            0,
            255);
    }

    private static int LayerChannelOffset(string channel)
    {
        return channel.Trim().ToLowerInvariant() switch
        {
            "g" => 1,
            "r" => 2,
            "a" => 3,
            _ => 0,
        };
    }

    private static Bitmap ScaleToBgra(Bitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height)
        {
            return source.Clone(new Rectangle(0, 0, width, height), PixelFormat.Format32bppArgb);
        }
        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var input = ReadBgra(source);
        var output = new byte[checked(width * height * 4)];
        var horizontal = new (int Left, int Right, float Weight)[width];
        var scaleX = source.Width / (float)width;
        var scaleY = source.Height / (float)height;
        for (var x = 0; x < width; x++)
        {
            var position = Math.Clamp((x + 0.5f) * scaleX - 0.5f, 0, source.Width - 1);
            var left = (int)position;
            horizontal[x] = (left * 4, Math.Min(left + 1, source.Width - 1) * 4, position - left);
        }
        // DDS channels are independent data. GDI's premultiplied-alpha scaling
        // changes RGB when alpha stores cutout coverage or packed specularity.
        for (var y = 0; y < height; y++)
        {
            var position = Math.Clamp((y + 0.5f) * scaleY - 0.5f, 0, source.Height - 1);
            var top = (int)position;
            var weightY = position - top;
            var row0 = top * source.Width * 4;
            var row1 = Math.Min(top + 1, source.Height - 1) * source.Width * 4;
            for (var x = 0; x < width; x++)
            {
                var (left, right, weightX) = horizontal[x];
                for (var channel = 0; channel < 4; channel++)
                {
                    var upper = input[row0 + left + channel] * (1 - weightX) + input[row0 + right + channel] * weightX;
                    var lower = input[row1 + left + channel] * (1 - weightX) + input[row1 + right + channel] * weightX;
                    output[(y * width + x) * 4 + channel] = (byte)MathF.Round(upper * (1 - weightY) + lower * weightY,
                        MidpointRounding.AwayFromZero);
                }
            }
        }
        WriteBgra(result, output);
        return result;
    }

    private static byte[] ReadBgra(Bitmap bitmap)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(bitmap.Width * 4);
            var result = new byte[checked(rowBytes * bitmap.Height)];
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), result, y * rowBytes, rowBytes);
            }
            return result;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static void WriteBgra(Bitmap bitmap, byte[] pixels)
    {
        var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = checked(bitmap.Width * 4);
            for (var y = 0; y < bitmap.Height; y++)
            {
                Marshal.Copy(pixels, y * rowBytes, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
