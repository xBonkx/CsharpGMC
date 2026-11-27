using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Live font-size + wrap bindings; no row recreation or SQL reload.</summary>
public static class AdaptiveGridRowHeight
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(AdaptiveGridRowHeight),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not DataGrid grid) return;
        if (!(bool)e.NewValue)
        {
            BindingOperations.ClearBinding(grid, DataGrid.MinRowHeightProperty);
            BindingOperations.ClearBinding(grid, DataGrid.RowHeightProperty);
            return;
        }
        BindingOperations.SetBinding(grid, DataGrid.MinRowHeightProperty, new Binding(nameof(Control.FontSize))
        {
            Source = grid, Mode = BindingMode.OneWay, Converter = new MinimumConverter()
        });
        var height = new MultiBinding { Mode = BindingMode.OneWay, Converter = new HeightConverter() };
        height.Bindings.Add(new Binding(nameof(Control.FontSize)) { Source = grid });
        height.Bindings.Add(new Binding(nameof(NameWrapPreference.IsEnabled)) { Source = NameWrapPreference.Current });
        BindingOperations.SetBinding(grid, DataGrid.RowHeightProperty, height);
    }

    private sealed class MinimumConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            TableRowHeightPolicy.Minimum(value is double size ? size : 40d / 3d);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }

    private sealed class HeightConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            TableRowHeightPolicy.RowHeight(values[0] is double size ? size : 40d / 3d, values[1] is true);
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
