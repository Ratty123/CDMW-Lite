namespace Cdmw.ArchiveLite.Core;

public static class ArchiveModelPreviewPolicy
{
    private static readonly HashSet<string> OverheadCameraFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon",
        "subweapon",
        "shield",
        "onehandweapon",
        "twohandweapon",
        "sword",
        "longsword",
        "greatsword",
        "dagger",
        "axe",
        "spear",
        "lance",
        "staff",
        "mace",
        "hammer",
        "bow",
        "crossbow",
        "musket",
        "cannon",
        "instrument",
    };

    public static ArchiveModelInitialView InitialView(string? sourcePath)
    {
        var overhead = UsesOverheadCamera(sourcePath);
        return new ArchiveModelInitialView(
            YawDegrees: 0.0f,
            PitchDegrees: overhead ? -89.0f : 0.0f,
            FitToView: true,
            Reason: overhead ? "archive_model_initial_overhead" : "archive_model_initial_front");
    }

    public static bool UsesOverheadCamera(string? sourcePath)
    {
        var normalized = (sourcePath ?? string.Empty).Replace('\\', '/').Trim().ToLowerInvariant();
        if (normalized.Length == 0)
        {
            return false;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var segmentCount = segments.Length > 1 ? segments.Length - 1 : segments.Length;
        for (var index = 0; index < segmentCount; index++)
        {
            var family = segments[index].TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '-');
            if (OverheadCameraFamilies.Contains(family))
            {
                return true;
            }
        }
        return false;
    }
}

public readonly record struct ArchiveModelInitialView(
    float YawDegrees,
    float PitchDegrees,
    bool FitToView,
    string Reason);
