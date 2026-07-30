using System.Text;

namespace Cdmw.ArchiveLite.App.Services;

/// <summary>
/// Turns the item catalog's canonical vocabulary into display text for the current language.
/// </summary>
/// <remarks>
/// <para>
/// Categories, groups, material tags, and evidence phrases are produced in English by the managed
/// classifier and the native accelerator, and they travel back to the worker unchanged as search
/// filters. Translating them at the source would break that round trip, so the canonical value stays
/// the identity and only the label is localized here.
/// </para>
/// <para>
/// Keys are derived from the value itself (<c>"Axe / Mace / Hammer"</c> becomes
/// <c>ItemGroupAxeMaceHammer</c>) rather than listed in a lookup table, so adding a resource string
/// is all it takes to translate a term. A value with no resource falls back to its canonical English,
/// which keeps an unrecognized term readable instead of showing a bracketed key; the vocabulary
/// below exists so the resource-parity test can prove the shipped terms are not relying on that.
/// </para>
/// </remarks>
public static class ItemCatalogLabels
{
    private const string CategoryPrefix = "ItemCategory";
    private const string GroupPrefix = "ItemGroup";
    private const string MaterialPrefix = "ItemMaterial";
    private const string EvidencePrefix = "ItemEvidence";
    private const string EvidenceSeparator = "; ";

    /// <summary>Every category the classifier can assign.</summary>
    public static IReadOnlyList<string> Categories { get; } =
    [
        "Accessory", "Armor", "Character Customization", "Consumable", "Crafting / Recipe",
        "Gimmick / Interactive", "Housing / Prop", "Item", "Material", "Mount / Pet",
        "Progression / Reward", "Quest / Document", "Tool", "Weapon",
    ];

    /// <summary>Every group the classifier can assign. Group names do not repeat across categories.</summary>
    public static IReadOnlyList<string> Groups { get; } =
    [
        "Amulet / Charm", "Artifact", "Axe / Mace / Hammer", "Back / Cloak", "Backpack / Pack",
        "Belt / Band", "Body", "Body / Appearance", "Book / Diary", "Bow / Crossbow",
        "Cloth / Leather", "Clue / Report", "Collection Prop", "Container", "Crafting",
        "Creature Part", "Crystal / Gem", "Currency", "Dagger / Rapier", "Decor", "Document",
        "Earrings", "Face", "Feet", "Firearm", "Fishing", "Fist / Martial", "Flag / Marker",
        "Food / Drink", "Furniture", "Gathering Tool", "Gimmick", "Hair", "Hand Tool", "Hands",
        "Head", "Horse Gear", "Key / Permit", "Legs", "Light / Lantern", "Machine Part",
        "Map / Treasure", "Necklace", "Ore / Metal", "Other Accessory", "Other Armor",
        "Other Consumable", "Other Material", "Other Tool", "Other Weapon", "Pet Gear",
        "Polearm / Spear", "Potion / Medicine", "Quest", "Recipe Book", "Reward", "Ring", "Shield",
        "Skill", "Stat", "Sword", "Throwable / Utility", "Token / Seal", "Unclassified", "Vehicle",
        "Wand / Fan", "Wood / Stone",
    ];

    /// <summary>The canonical material tags the native accelerator folds its aliases onto.</summary>
    public static IReadOnlyList<string> MaterialTags { get; } =
    [
        "bone", "cloth", "crystal", "dirt", "fur", "glass", "grass", "hair", "leather", "metal",
        "rope", "skin", "stone", "water", "wood",
    ];

    /// <summary>
    /// Every evidence phrase, covering both the parts joined into an item's evidence line and the
    /// whole-string category evidence.
    /// </summary>
    public static IReadOnlyList<string> EvidencePhrases { get; } =
    [
        "ItemInfo prefab hash", "icon/model reference", "localized display name",
        "inventory icon path", "material slot tags", "item database record",
        "Recovered item/model naming", "No stronger category evidence was recovered",
    ];

    public static string Category(string? value) => Localize(CategoryPrefix, value);

    public static string Group(string? value) => Localize(GroupPrefix, value);

    public static string MaterialTag(string? value) => Localize(MaterialPrefix, value);

    /// <summary>Formats the "category / group" pair a tile and the detail pane both show.</summary>
    public static string CategoryPath(string? category, string? group) =>
        $"{Category(category)} / {Group(group)}";

    /// <summary>
    /// Localizes an evidence line. The classifier joins several phrases with "; ", so each part is
    /// resolved on its own and the line is rebuilt.
    /// </summary>
    public static string Evidence(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : string.Join(
                EvidenceSeparator,
                value.Split(EvidenceSeparator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(static part => Localize(EvidencePrefix, part)));

    /// <summary>
    /// The resource key a canonical value resolves to, exposed so the resource-parity test can check
    /// the shipped vocabulary without duplicating the derivation.
    /// </summary>
    public static string CategoryKey(string value) => CategoryPrefix + Slug(value);

    public static string GroupKey(string value) => GroupPrefix + Slug(value);

    public static string MaterialTagKey(string value) => MaterialPrefix + Slug(value);

    public static string EvidenceKey(string value) => EvidencePrefix + Slug(value);

    private static string Localize(string prefix, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var canonical = value.Trim();
        return LocalizationManager.Find(prefix + Slug(canonical)) ?? canonical;
    }

    /// <summary>
    /// Folds a canonical value into a key suffix: words split on anything that is not a letter or
    /// digit, each capitalized and joined. "icon/model reference" becomes "IconModelReference".
    /// </summary>
    private static string Slug(string value)
    {
        var slug = new StringBuilder(value.Length);
        var startingWord = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                startingWord = true;
                continue;
            }

            slug.Append(startingWord ? char.ToUpperInvariant(character) : character);
            startingWord = false;
        }

        return slug.ToString();
    }
}
