namespace Cdmw.ArchiveLite.App.Services;

public static class CharacterFinderLabels
{
    public static string Value(string value) => value switch
    {
        "body" => ItemCatalogLabels.Group("Body"), "head" => ItemCatalogLabels.Group("Head"),
        "hair" => ItemCatalogLabels.Group("Hair"), "unclassified" => ItemCatalogLabels.Group("Unclassified"),
        "whole_character" => LocalizationManager.Get("CharacterWhole"),
        "facial_detail" => LocalizationManager.Get("CharacterFacialDetails"),
        "beard" => LocalizationManager.Get("CharacterBeard"),
        "humanoid" => LocalizationManager.Get("CharacterHumanoids"),
        "1_pc" => LocalizationManager.Get("CharacterPlayers"),
        "2_mon" => LocalizationManager.Get("CharacterCreatures"),
        "3_npc" => LocalizationManager.Get("CharacterNpcs"),
        "4_riding" => LocalizationManager.Get("CharacterMounts"),
        "resolved" => LocalizationManager.Get("CharacterResolved"),
        "unresolved" => LocalizationManager.Get("CharacterUnresolved"),
        "inferred" => LocalizationManager.Get("CharacterInferred"),
        "ambiguous" => LocalizationManager.Get("CharacterAmbiguous"),
        "unknown" => LocalizationManager.Get("CharacterUnknown"),
        _ => value,
    };
}
