using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Cdmw.ArchiveLite.App.Services;

public static class LocalizationManager
{
    private static readonly ResourceManager Resources = new(
        "Cdmw.ArchiveLite.App.Resources.Strings",
        Assembly.GetExecutingAssembly());
    private static LanguageDefinition _selectedLanguage = LanguageCatalog.Default;
    private static CultureInfo _selectedCulture = CultureInfo.GetCultureInfo(LanguageCatalog.Default.Code);

    /// <summary>The shipped language currently applied, including the font stack it needs.</summary>
    public static LanguageDefinition CurrentLanguage => Volatile.Read(ref _selectedLanguage);

    public static void ApplyCulture(string? language)
    {
        var definition = LanguageCatalog.Resolve(language);
        var culture = CreateCulture(definition.Code);
        Volatile.Write(ref _selectedLanguage, definition);
        Volatile.Write(ref _selectedCulture, culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        UiFontManager.Apply(definition);
        LocalizedStringSource.Instance.Refresh();
    }

    public static string Get(string key) => Resources.GetString(key, Volatile.Read(ref _selectedCulture)) ?? $"[{key}]";

    public static string Format(string key, params object?[] values) =>
        string.Format(Volatile.Read(ref _selectedCulture), Get(key), values);

    /// <summary>
    /// Builds the culture for a shipped language. A machine running in globalization-invariant mode
    /// cannot construct specific cultures; falling back keeps the app starting in English rather
    /// than throwing before the window exists.
    /// </summary>
    private static CultureInfo CreateCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
