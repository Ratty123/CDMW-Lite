namespace Cdmw.ArchiveLite.App.Infrastructure;

public static class WorkspacePaneSizing
{
    public const double MaximumPreviewWidth = 1000d;

    public static double CalculatePreviewMaximum(
        double workspaceWidth,
        double filterWidth,
        double resultsMinimumWidth,
        double splitterWidth,
        double previewMinimumWidth)
    {
        var minimum = NormalizeNonNegative(previewMinimumWidth);
        if (!double.IsFinite(workspaceWidth) || workspaceWidth <= 0)
        {
            return MaximumPreviewWidth;
        }

        var available = workspaceWidth
            - NormalizeNonNegative(filterWidth)
            - NormalizeNonNegative(resultsMinimumWidth)
            - NormalizeNonNegative(splitterWidth);
        return Math.Clamp(available, minimum, Math.Max(minimum, MaximumPreviewWidth));
    }

    private static double NormalizeNonNegative(double value) =>
        double.IsFinite(value) ? Math.Max(0d, value) : 0d;
}
