using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GmcSmeta.App;

/// <summary>Python QMenu glyphs: 18 DIP, muted outline, red only for dangerous actions.</summary>
internal static class ContextMenuIcons
{
    // Only separators collapsed by this helper are restored on the next opening.
    // Explicit Visibility values/bindings are respected. Never mutate ItemsSource.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Separator, object>
        HiddenSeparators = new();

    internal static void InstallSeparatorNormalization()
    {
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent,
            new RoutedEventHandler(NormalizeOnOpen));
        EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.SubmenuOpenedEvent,
            new RoutedEventHandler(NormalizeOnOpen));
    }

    private static void NormalizeOnOpen(object sender, RoutedEventArgs e)
    {
        if (sender is ItemsControl menu && ReferenceEquals(e.OriginalSource, menu))
            NormalizeSeparators(menu);
    }

    internal static void NormalizeSeparators(ItemsControl menu)
    {
        if (menu.ItemsSource is not null) return;
        foreach (var separator in menu.Items.OfType<Separator>())
        {
            if (!HiddenSeparators.Remove(separator)) continue;
            if (separator.ReadLocalValue(UIElement.VisibilityProperty) is Visibility.Collapsed)
                separator.ClearValue(UIElement.VisibilityProperty);
        }

        bool hasItem = false;
        Separator? pending = null;
        void Hide(Separator separator)
        {
            if (separator.ReadLocalValue(UIElement.VisibilityProperty) != DependencyProperty.UnsetValue)
                return;
            separator.Visibility = Visibility.Collapsed;
            HiddenSeparators.GetValue(separator, _ => new object());
        }
        foreach (var item in menu.Items)
        {
            if (item is UIElement { Visibility: not Visibility.Visible }) continue;
            if (item is Separator separator)
            {
                if (!hasItem || pending is not null) Hide(separator);
                else pending = separator;
                continue;
            }
            hasItem = true;
            pending = null;
        }
        if (pending is not null) Hide(pending);
    }

    internal static MenuItem Create(string title, string glyph, bool enabled = true,
        string shortcut = "", bool danger = false)
    {
        var item = new MenuItem { Header = title, IsEnabled = enabled, InputGestureText = shortcut };
        Set(item, glyph, danger);
        return item;
    }

    private static readonly Brush NormalStroke = FrozenStroke(0x64, 0x74, 0x8B);
    private static readonly Brush DangerStroke = FrozenStroke(0xDC, 0x26, 0x26);
    private static readonly Brush HighlightStroke = FrozenStroke(0x1E, 0x40, 0xAF);
    private static readonly Brush DisabledStroke = FrozenStroke(0x94, 0xA3, 0xB8);

    private static Brush FrozenStroke(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    internal static void Set(MenuItem item, string glyph, bool danger = false)
    {
        if (Application.Current.TryFindResource("Glyph" + glyph) is not Geometry geometry ||
            Application.Current.TryFindResource("SvgGlyphPath") is not Style baseStyle)
            return;

        var style = new Style(typeof(System.Windows.Shapes.Path), baseStyle);
        style.Setters.Add(new Setter(Shape.StrokeProperty,
            danger ? DangerStroke : NormalStroke));
        var highlight = new DataTrigger
        {
            Binding = new Binding(nameof(MenuItem.IsHighlighted)) { Source = item },
            Value = true
        };
        if (!danger)
        {
            highlight.Setters.Add(new Setter(Shape.StrokeProperty,
                HighlightStroke));
            style.Triggers.Add(highlight);
        }
        var disabled = new DataTrigger
        {
            Binding = new Binding(nameof(MenuItem.IsEnabled)) { Source = item },
            Value = false
        };
        disabled.Setters.Add(new Setter(Shape.StrokeProperty,
            DisabledStroke));
        style.Triggers.Add(disabled);
        item.Icon = VectorGlyph.Create(geometry, style);
    }
}
