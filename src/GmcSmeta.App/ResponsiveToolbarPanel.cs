using System;
using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Two toolbar groups: use one line when both fit, otherwise give each
/// group the full available width. Children own their own wrapping.</summary>
public sealed class ResponsiveToolbarPanel : Panel
{
    private const double Gap = 10;
    private double _leftWidth;
    private double _rightWidth;
    private bool _stacked;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count != 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Measure(availableSize);
            return new Size();
        }

        var left = InternalChildren[0];
        var right = InternalChildren[1];
        var unconstrained = new Size(double.PositiveInfinity, double.PositiveInfinity);
        left.Measure(unconstrained);
        right.Measure(unconstrained);
        var leftPreferred = left.DesiredSize.Width;
        var rightPreferred = right.DesiredSize.Width;
        var preferred = leftPreferred + Gap + rightPreferred;
        var width = double.IsInfinity(availableSize.Width) ? preferred : availableSize.Width;
        _stacked = preferred > width;
        _rightWidth = _stacked ? width : Math.Min(rightPreferred, width);
        _leftWidth = _stacked ? width : Math.Max(0, width - _rightWidth - Gap);
        left.Measure(new Size(_leftWidth, double.PositiveInfinity));
        right.Measure(new Size(_rightWidth, double.PositiveInfinity));
        var height = _stacked
            ? left.DesiredSize.Height + right.DesiredSize.Height
            : Math.Max(left.DesiredSize.Height, right.DesiredSize.Height);
        return new Size(Math.Min(preferred, width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (InternalChildren.Count != 2)
        {
            foreach (UIElement child in InternalChildren)
                child.Arrange(new Rect(finalSize));
            return finalSize;
        }

        var left = InternalChildren[0];
        var right = InternalChildren[1];
        if (_stacked)
        {
            left.Arrange(new Rect(0, 0, finalSize.Width, left.DesiredSize.Height));
            right.Arrange(new Rect(0, left.DesiredSize.Height, finalSize.Width, right.DesiredSize.Height));
        }
        else
        {
            var rightWidth = Math.Min(_rightWidth, finalSize.Width);
            var leftWidth = Math.Max(0, finalSize.Width - rightWidth - Gap);
            left.Arrange(new Rect(0, 0, leftWidth, finalSize.Height));
            right.Arrange(new Rect(finalSize.Width - rightWidth, 0, rightWidth, finalSize.Height));
        }
        return finalSize;
    }
}
