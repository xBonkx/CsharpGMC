using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GmcSmeta.App;

/// <summary>Отступ названия по визуальному уровню (шаг 14 DIP, как в Python).</summary>
public sealed class LevelIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        return new Thickness(Math.Max(0, level) * 14, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
