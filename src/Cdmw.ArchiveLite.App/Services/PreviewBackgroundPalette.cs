using System.Globalization;
using System.Windows.Media;

namespace Cdmw.ArchiveLite.App.Services;

/// <summary>
/// Resolves a <see cref="PreviewBackgroundChoice"/> to the sRGB colour drawn behind a preview. The
/// presets are neutral steps plus magenta, because a texture read against a single dark surface hides
/// both its own dark pixels and its transparent ones.
/// </summary>
public static class PreviewBackgroundPalette
{
    public const string DefaultCustomColor = "#202020";

    /// <summary>
    /// Returns false when the theme keeps the surface, so callers leave the themed brush in place
    /// instead of freezing one theme's colour.
    /// </summary>
    public static bool TryResolve(PreviewBackgroundChoice choice, string? customColor, out Color color)
    {
        switch (choice)
        {
            case PreviewBackgroundChoice.Black:
                color = Color.FromRgb(0x00, 0x00, 0x00);
                return true;
            case PreviewBackgroundChoice.Charcoal:
                color = Color.FromRgb(0x1E, 0x1E, 0x1E);
                return true;
            case PreviewBackgroundChoice.MidGray:
                color = Color.FromRgb(0x80, 0x80, 0x80);
                return true;
            case PreviewBackgroundChoice.LightGray:
                color = Color.FromRgb(0xD0, 0xD0, 0xD0);
                return true;
            case PreviewBackgroundChoice.White:
                color = Color.FromRgb(0xFF, 0xFF, 0xFF);
                return true;
            case PreviewBackgroundChoice.Magenta:
                color = Color.FromRgb(0xFF, 0x00, 0xFF);
                return true;
            case PreviewBackgroundChoice.Custom:
                // A half-typed colour keeps the theme surface rather than flashing an unrelated one.
                return TryParseHex(customColor, out color);
            default:
                color = default;
                return false;
        }
    }

    /// <summary>Keeps a storable #RRGGBB, falling back to the default rather than persisting junk.</summary>
    public static string NormalizeCustomColor(string? customColor) =>
        TryParseHex(customColor, out var color)
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : DefaultCustomColor;

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var value = (text ?? string.Empty).Trim();
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }
        if (value.Length == 3)
        {
            // #abc is the same colour as #aabbcc; accept it so typing stays forgiving.
            value = string.Concat(value[0], value[0], value[1], value[1], value[2], value[2]);
        }
        if (value.Length != 6
            || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
        {
            return false;
        }
        color = Color.FromRgb(
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)(packed & 0xFF));
        return true;
    }
}
