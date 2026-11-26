using System.Globalization;
using System.Windows.Data;

namespace GmcSmeta.App;

/// <summary>Keep ordinary live scrolling for small projections. On large
/// estimates WPF moves the thumb immediately but lays out the destination
/// only on release; wheel, keys and programmatic scrolling remain live.</summary>
public sealed class EstimateScrollPolicy : IValueConverter
{
    internal const int DeferredRowThreshold = 2500;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count >= DeferredRowThreshold;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
