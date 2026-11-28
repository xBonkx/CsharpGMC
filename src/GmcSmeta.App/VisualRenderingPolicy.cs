using System.Windows;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Apply shared rendering defaults to dynamic and XAML-created roots, without replacing
/// local bindings or deliberate style exceptions (e.g. Ideal/Grayscale popup text).</summary>
internal static class VisualRenderingPolicy
{
    internal static void Install()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnWindowLoaded));
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is Window window && ReferenceEquals(args.OriginalSource, window))
            Apply(window);
    }

    internal static void Apply(FrameworkElement root)
    {
        Default(root, FrameworkElement.UseLayoutRoundingProperty, true);
        Default(root, UIElement.SnapsToDevicePixelsProperty, true);
        Default(root, TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        Default(root, TextOptions.TextRenderingModeProperty, TextRenderingMode.ClearType);
    }

    private static void Default(DependencyObject target, DependencyProperty property, object value)
    {
        var source = DependencyPropertyHelper.GetValueSource(target, property);
        if (!source.IsExpression && !source.IsAnimated &&
            source.BaseValueSource is BaseValueSource.Default or BaseValueSource.Inherited)
            target.SetCurrentValue(property, value);
    }
}
