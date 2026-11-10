namespace AssetScope.Core;

/// <summary>
/// Shared page-shell measurements. Page SizeChanged reports the full Frame
/// viewport, while dashboard columns live inside the shell padding. Responsive
/// decisions must use the latter or they switch layouts too late and overflow.
/// </summary>
public static class PageLayoutMetrics
{
    public const double StandardHorizontalPadding = 56;
    public const double StandardColumnSpacing = 16;

    public static double ContentWidth(
        double pageWidth,
        double horizontalPadding = StandardHorizontalPadding) =>
        Math.Max(0, pageWidth - horizontalPadding);

    public static bool ShouldStack(
        double pageWidth,
        double minimumTwoColumnContentWidth,
        double horizontalPadding = StandardHorizontalPadding) =>
        ContentWidth(pageWidth, horizontalPadding) < minimumTwoColumnContentWidth;

    public static double PrimaryColumnWidth(
        double pageWidth,
        double secondaryColumnWidth,
        bool stacked,
        double horizontalPadding = StandardHorizontalPadding) =>
        stacked
            ? ContentWidth(pageWidth, horizontalPadding)
            : Math.Max(0, ContentWidth(pageWidth, horizontalPadding)
                - secondaryColumnWidth - StandardColumnSpacing);
}
