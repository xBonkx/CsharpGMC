using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace GmcSmeta.App;

/// <summary>Отступ названия по визуальному уровню (шаг 14 DIP, как в Python).</summary>
public sealed class LevelIndentConverter : IValueConverter
{
    // Common indentation levels are immutable boxed values. Keep a bounded
    // table rather than allocate one boxed Thickness per visible cell.
    private static readonly object[] Common = Enumerable.Range(0, 16)
        .Select(level => (object)new Thickness(level * 14, 0, 0, 0)).ToArray();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var level = Math.Max(0, value is int i ? i : 0);
        return level < Common.Length ? Common[level] : new Thickness(level * 14, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
