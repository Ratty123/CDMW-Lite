using System.Windows;
using System.Windows.Media;

namespace Cdmw.ArchiveLite.App.Services;

/// <summary>
/// Publishes the font stacks the active language needs as application resources.
/// </summary>
/// <remarks>
/// The shell binds <c>UiFontFamily</c> and <c>MonoFontFamily</c> dynamically, so switching language
/// re-faces the whole window without a restart. Both keys are always set, so a control can never
/// resolve one and miss the other.
/// </remarks>
public static class UiFontManager
{
    private static readonly Dictionary<string, FontFamily> Cache = new(StringComparer.Ordinal);

    public static void Apply(LanguageDefinition language)
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        resources["UiFontFamily"] = Resolve(language.UiFontStack);
        resources["MonoFontFamily"] = Resolve(language.EditorFontStack);
    }

    /// <summary>
    /// A comma-separated stack is a WPF fallback list: the first family the machine actually has
    /// wins, and missing families are skipped without error.
    /// </summary>
    private static FontFamily Resolve(string stack)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(stack, out var family))
            {
                family = new FontFamily(stack);
                Cache[stack] = family;
            }

            return family;
        }
    }
}
