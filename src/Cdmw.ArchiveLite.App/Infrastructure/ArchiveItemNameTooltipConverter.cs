using System.Globalization;
using System.Windows.Data;
using Cdmw.ArchiveLite.App.Services;
using Cdmw.ArchiveLite.Contracts;

namespace Cdmw.ArchiveLite.App.Infrastructure;

/// <summary>
/// Says where a row's item name came from. The grid shows one merged name column, so the
/// distinction the separate evidence column used to carry — a name the archive states outright
/// against a likely name inferred from a related asset — lives in the cell's tooltip.
/// </summary>
public sealed class ArchiveItemNameTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not ArchiveEntryDto entry || string.IsNullOrWhiteSpace(entry.ItemName)
            ? null
            : LocalizationManager.Get(entry.HasExactItemName ? "ItemNameExactHint" : "ItemNameEvidenceHint");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
