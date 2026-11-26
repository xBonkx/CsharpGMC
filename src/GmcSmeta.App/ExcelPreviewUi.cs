using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Presentation only: no import decisions or database access.</summary>
internal static class ExcelPreviewUi
{
    internal static GridSplitter ReportSplitter() => new()
    {
        Height = 8, HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Rows,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Background = new SolidColorBrush(Color.FromRgb(218, 227, 239)),
        ShowsPreview = true
    };

    internal static Style StatusStyle(Style baseStyle, string bindingPath,
        params (object Value, string Color)[] states)
    {
        var style = new Style(typeof(TextBlock), baseStyle);
        foreach (var state in states)
        {
            var trigger = new DataTrigger { Binding = new Binding(bindingPath), Value = state.Value };
            trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                (Brush)new BrushConverter().ConvertFromString(state.Color)!));
            style.Triggers.Add(trigger);
        }
        // Selection wins over semantic colors, including on recycled cells.
        var selected = new DataTrigger
        {
            Binding = new Binding(nameof(DataGridCell.IsSelected))
            { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1) },
            Value = true
        };
        selected.Setters.Add(new Setter(TextBlock.ForegroundProperty,
            Application.Current.FindResource("GridSelectionTextBrush")));
        style.Triggers.Add(selected);
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty,
            new Binding(nameof(TextBlock.Text)) { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }));
        return style;
    }

    internal sealed class ChangeLabelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            ExcelExistingChangeKind.Added => "Новая позиция",
            ExcelExistingChangeKind.Changed => "Изменено",
            ExcelExistingChangeKind.Unchanged => "Без изменений",
            ExcelExistingChangeKind.DeletedInSource => "Нет в Excel",
            ExcelExistingChangeKind.Ambiguous => "Неоднозначно",
            ExcelExistingChangeKind.Conflict => "Конфликт",
            _ => value?.ToString() ?? ""
        };

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
