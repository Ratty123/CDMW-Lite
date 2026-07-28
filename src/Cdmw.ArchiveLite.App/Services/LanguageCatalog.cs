namespace Cdmw.ArchiveLite.App.Services;

/// <summary>
/// One shipped UI language: the culture its satellite resources compile under, the endonym the
/// picker shows, and the font stacks that culture needs.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Code"/> is a real .NET culture name, so it doubles as the resource suffix
/// (<c>Strings.pt-BR.resx</c>) and the satellite assembly folder. Region- and script-qualified codes
/// are deliberate: Latin-American Spanish and Brazilian Portuguese are separate shipped languages,
/// and Simplified and Traditional Chinese cannot share one file.
/// </para>
/// <para>
/// Segoe UI carries no CJK glyphs. Left alone, WPF substitutes per glyph, which mixes metrics inside
/// a single label; naming a stack keeps one face across a run of text. The stack is a WPF fallback
/// list, so entries the machine lacks are skipped rather than failing.
/// </para>
/// </remarks>
public sealed record LanguageDefinition(
    string Code,
    string Endonym,
    string UiFontStack,
    string EditorFontStack);

/// <summary>
/// The single source of truth for which languages ship. The picker, the culture resolver, the font
/// stacks, and the resource-parity test all read this list, so a language cannot be half-added.
/// </summary>
public static class LanguageCatalog
{
    private const string LatinUiFonts = "Segoe UI";
    private const string LatinEditorFonts = "Consolas";

    /// <summary>
    /// Matches the fourteen interface and subtitle languages Crimson Desert ships. None of them are
    /// right-to-left, so the shell needs no bidirectional layout work.
    /// </summary>
    public static IReadOnlyList<LanguageDefinition> Languages { get; } =
    [
        new("en", "English", LatinUiFonts, LatinEditorFonts),
        new("de", "Deutsch", LatinUiFonts, LatinEditorFonts),
        new("es", "Español", LatinUiFonts, LatinEditorFonts),
        new("es-419", "Español (LA)", LatinUiFonts, LatinEditorFonts),
        new("fr", "Français", LatinUiFonts, LatinEditorFonts),
        new("it", "Italiano", LatinUiFonts, LatinEditorFonts),
        new("pl", "Polski", LatinUiFonts, LatinEditorFonts),
        new("pt-BR", "Português (BR)", LatinUiFonts, LatinEditorFonts),
        new("ru", "Русский", LatinUiFonts, LatinEditorFonts),
        new("tr", "Türkçe", LatinUiFonts, LatinEditorFonts),
        new("ja", "日本語", "Segoe UI, Yu Gothic UI, Meiryo UI, MS UI Gothic", "Consolas, MS Gothic, Yu Gothic UI"),
        new("ko", "한국어", "Segoe UI, Malgun Gothic, Gulim", "Consolas, Malgun Gothic, GulimChe"),
        new("zh-Hans", "简体中文", "Segoe UI, Microsoft YaHei UI, Microsoft YaHei, SimSun", "Consolas, Microsoft YaHei, NSimSun"),
        new("zh-Hant", "繁體中文", "Segoe UI, Microsoft JhengHei UI, Microsoft JhengHei, PMingLiU", "Consolas, Microsoft JhengHei, MingLiU"),
    ];

    public static LanguageDefinition Default { get; } = Languages[0];

    /// <summary>
    /// Resolves a stored or system culture name onto a shipped language. Regional and legacy codes
    /// are folded onto the language that actually covers them, so a settings file written by an
    /// older build - or a machine running under <c>zh-CN</c> - lands somewhere sensible instead of
    /// silently reverting to English.
    /// </summary>
    public static LanguageDefinition Resolve(string? code)
    {
        var requested = code?.Trim();
        if (string.IsNullOrEmpty(requested))
        {
            return Default;
        }

        var exact = Languages.FirstOrDefault(
            language => language.Code.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var alias = ResolveAlias(requested);
        if (alias is not null)
        {
            return Languages.First(language => language.Code.Equals(alias, StringComparison.Ordinal));
        }

        // Fall back to the bare language part, so an unlisted region such as fr-CA still gets French.
        var separator = requested.IndexOf('-');
        if (separator > 0)
        {
            var bare = requested[..separator];
            var parent = Languages.FirstOrDefault(
                language => language.Code.Equals(bare, StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                return parent;
            }
        }

        return Default;
    }

    private static string? ResolveAlias(string requested) => requested.ToLowerInvariant() switch
    {
        "es-es" => "es",
        // Every Latin-American Spanish locale shares the es-419 wording.
        "es-mx" or "es-ar" or "es-co" or "es-cl" or "es-pe" or "es-us" or "es-419" => "es-419",
        // Portugal is not a shipped locale; Brazilian Portuguese is the closest shipped wording.
        "pt" or "pt-pt" => "pt-BR",
        "zh" or "zh-cn" or "zh-sg" or "zh-chs" or "zh-hans-cn" => "zh-Hans",
        "zh-tw" or "zh-hk" or "zh-mo" or "zh-cht" or "zh-hant-tw" => "zh-Hant",
        _ => null,
    };
}
