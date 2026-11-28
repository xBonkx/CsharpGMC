using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>One-line document metadata; long object/company names yield space
/// before status chips. Hidden chips reserve no width. Tooltips retain full text.</summary>
public sealed class DocumentCrumbPanel : Panel
{
    public static readonly DependencyProperty CanShrinkProperty =
        DependencyProperty.RegisterAttached("CanShrink", typeof(bool), typeof(DocumentCrumbPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetCanShrink(DependencyObject element) => (bool)element.GetValue(CanShrinkProperty);
    public static void SetCanShrink(DependencyObject element, bool value) => element.SetValue(CanShrinkProperty, value);

    private double[] _widths = Array.Empty<double>();

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren.Cast<UIElement>().ToArray();
        _widths = new double[children.Length];
        for (var i = 0; i < children.Length; i++)
        {
            children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _widths[i] = children[i].DesiredSize.Width;
        }

        if (double.IsFinite(availableSize.Width))
        {
            var overflow = Math.Max(0, _widths.Sum() - availableSize.Width);
            // First shrink metadata labels proportionally, then remaining badges only
            // if the viewport is too narrow even for the status text.
            foreach (var shrinkable in new[] { true, false })
            {
                if (overflow <= .001) break;
                var group = Enumerable.Range(0, children.Length)
                    .Where(i => GetCanShrink(children[i]) == shrinkable).ToArray();
                var total = group.Sum(i => _widths[i]);
                if (total <= 0) continue;
                var removed = Math.Min(overflow, total);
                foreach (var i in group)
                    _widths[i] = Math.Max(0, _widths[i] * (1 - removed / total));
                overflow -= removed;
            }
        }

        double height = 0;
        for (var i = 0; i < children.Length; i++)
        {
            children[i].Measure(new Size(_widths[i], double.PositiveInfinity));
            height = Math.Max(height, children[i].DesiredSize.Height);
        }
        return new Size(_widths.Sum(), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var remaining = Math.Max(0, finalSize.Width - x);
            var width = i < _widths.Length ? Math.Min(_widths[i], remaining) : 0;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
