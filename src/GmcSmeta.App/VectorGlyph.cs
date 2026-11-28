using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GmcSmeta.App;

/// <summary>One geometry, stroke and 24-unit canvas for code-created glyphs.
/// Glyph sizes are independent of their parent's input target.</summary>
internal static class VectorGlyph
{
    internal static Viewbox Create(Geometry geometry, Style pathStyle, Brush? stroke = null,
        double size = 18)
    {
        var path = new System.Windows.Shapes.Path { Data = geometry, Style = pathStyle };
        if (stroke is not null) path.Stroke = stroke;
        var canvas = new Canvas { Width = 24, Height = 24, IsHitTestVisible = false };
        canvas.Children.Add(path);
        var view = new Viewbox { Width = size, Height = size, Child = canvas,
            IsHitTestVisible = false, Stretch = Stretch.Uniform };
        view.SetResourceReference(FrameworkElement.StyleProperty, "SvgGlyphViewbox");
        return view;
    }
}
