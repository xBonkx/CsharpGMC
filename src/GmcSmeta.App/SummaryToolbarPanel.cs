using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Two logical groups, left actions and right filters. On a narrow
/// viewport each group wraps independently, with filters on a second row.</summary>
public sealed class SummaryToolbarPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
        {
            foreach (UIElement child in InternalChildren) child.Measure(new Size(0, 0));
            return new Size();
        }
        var left = InternalChildren[0];
        var right = InternalChildren[1];
        var limit = new Size(availableSize.Width, double.PositiveInfinity);
        left.Measure(limit);
        right.Measure(limit);
        var a = left.DesiredSize;
        var b = right.DesiredSize;
        return SummaryToolbarLayout.Fits(availableSize.Width, a.Width, b.Width)
            ? new Size(a.Width + SummaryToolbarLayout.Gap + b.Width, Math.Max(a.Height, b.Height))
            : new Size(Math.Max(a.Width, b.Width), a.Height + SummaryToolbarLayout.Gap + b.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2) return finalSize;
        var left = InternalChildren[0];
        var right = InternalChildren[1];
        var a = left.DesiredSize;
        var b = right.DesiredSize;
        var sameRow = SummaryToolbarLayout.Fits(finalSize.Width, a.Width, b.Width);
        var widthA = Math.Min(a.Width, finalSize.Width);
        var widthB = Math.Min(b.Width, finalSize.Width);
        var height = Math.Max(a.Height, b.Height);
        left.Arrange(new Rect(0, sameRow ? (height - a.Height) / 2 : 0, widthA, a.Height));
        right.Arrange(new Rect(finalSize.Width - widthB,
            sameRow ? (height - b.Height) / 2 : a.Height + SummaryToolbarLayout.Gap,
            widthB, b.Height));
        return finalSize;
    }
}
