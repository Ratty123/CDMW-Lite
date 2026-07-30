using System.Text.RegularExpressions;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.Core;

/// <summary>
/// Decides whether an entry passes a filter. The entry list and the folder tree both narrow
/// themselves through here, so a filter cannot mean one thing in the list and another in the tree.
/// </summary>
public static class ArchiveEntryMatcher
{
    public static bool Matches(ArchiveEntryDto entry, ArchiveEntryFilter filter) =>
        MatchesExceptRole(entry, filter) && MatchesRole(entry, filter);

    public static bool MatchesExceptRole(ArchiveEntryDto entry, ArchiveEntryFilter filter) =>
        MatchesExceptRoleAndFolder(entry, filter) && MatchesFolder(entry, filter);

    /// <summary>
    /// Everything but the role and the folder. The folder is separate because the folder tree is how
    /// a folder is chosen and so narrows itself by everything except that, while the entry list
    /// narrows itself by all of it - and both want the answer from one pass over the archive.
    /// </summary>
    public static bool MatchesExceptRoleAndFolder(ArchiveEntryDto entry, ArchiveEntryFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!MatchesPath(entry, filter.PathText)) return false;
        if (filter.Extensions is { Count: > 0 } && !filter.Extensions.Any(value => MatchesExtension(entry.Extension, value))) return false;
        if (!string.IsNullOrWhiteSpace(filter.Package) && !entry.Package.Contains(filter.Package, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter.MinimumSize is { } minimum && entry.OriginalSize < minimum) return false;
        return !filter.PreviewableOnly || entry.IsPreviewable;
    }

    public static bool MatchesFolder(ArchiveEntryDto entry, ArchiveEntryFilter filter) =>
        string.IsNullOrWhiteSpace(filter.Folder)
        || entry.Path.StartsWith(filter.Folder.Trim().Replace('\\', '/').Trim('/') + "/", StringComparison.OrdinalIgnoreCase);

    public static bool MatchesRole(ArchiveEntryDto entry, ArchiveEntryFilter filter) =>
        filter.Roles is not { Count: > 0 } || filter.Roles.Contains(entry.Role);

    public static bool MatchesExtension(string extension, string candidate)
    {
        var normalized = candidate.Trim().ToLowerInvariant();
        if (normalized is "*" or ".*" or "all") return true;
        if (!normalized.StartsWith('.')) normalized = "." + normalized;
        return extension.Equals(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesPath(ArchiveEntryDto entry, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var text = filter.Trim();
        if (!text.ContainsAny(['*', '?', '[']))
        {
            return entry.Path.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.KnownName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || entry.NameEvidence.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        var pattern = "^" + Regex.Escape(text).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(entry.Path, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
            || Regex.IsMatch(entry.Name, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
            || Regex.IsMatch(entry.KnownName, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250))
            || Regex.IsMatch(entry.NameEvidence, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }
}
