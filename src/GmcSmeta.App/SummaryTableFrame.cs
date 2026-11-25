using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>A single rounded table border. WPF Border does not clip its child
/// to CornerRadius by default, so use a matching outer clip on resize.</summary>
public sealed class SummaryTableFrame : Border
{
    public SummaryTableFrame()
    {
        SizeChanged += (_, _) => UpdateRoundedClip();
        Loaded += (_, _) => UpdateRoundedClip();
    }

    internal void UpdateRoundedClip()
    {
        var radius = Math.Min(CornerRadius.TopLeft, Math.Min(ActualWidth, ActualHeight) / 2);
        var geometry = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), radius, radius);
        geometry.Freeze();
        Clip = geometry;
    }
}
