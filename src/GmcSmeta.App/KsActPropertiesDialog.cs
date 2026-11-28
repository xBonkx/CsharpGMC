using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal static class KsActPropertiesDialog
{
    internal static (SqlServerActs.ActDraftEdit Edit, string Message)? Show(FrameworkElement owner,
        SqlServerActs.ActDetails details, bool showDs)
    {
        var dialog = new Window
        {
            Title = "Свойства акта",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(owner),
        };
        DynamicFormLayout.Apply(dialog);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        var row = 0;
        TextBox Field(string caption, string text)
        {
            var label = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            label.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormLabelMargin");
            var box = new TextBox { Text = text };
            box.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFieldMargin");
            DynamicFormLayout.Field(box);
            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(label);
            grid.Children.Add(box);
            row += 1;
            return box;
        }
        var num = Field("Номер КС-2", details.Number);
        var dt = Field("Дата акта", details.Date.ToString("dd.MM.yyyy"));
        var fr = Field("Период с", details.PeriodFrom.ToString("dd.MM.yyyy"));
        var to = Field("по", details.PeriodTo.ToString("dd.MM.yyyy"));
        TextBox? dsNo = null, dsDate = null, dsExtra = null;
        if (showDs)
        {
            dsNo = Field("Доп. соглашение №", details.DsNo);
            dsDate = Field("от", details.DsDate);
            dsExtra = Field("Другие ДС (если есть)", details.DsExtra);
            dsExtra.AcceptsReturn = true;
            dsExtra.MinHeight = 64;
            dsExtra.MaxHeight = 180;
            dsExtra.TextWrapping = TextWrapping.Wrap;
            dsExtra.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        var ok = new Button
        {
            Content = "Сохранить",
            Style = (Style)owner.FindResource("PythonToolbarPrimary"),
            Padding = new Thickness(14, 5, 14, 5),
        };
        var cancel = new Button { Content = "Отмена", Style = (Style)owner.FindResource("PythonToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dialog.Content = DynamicFormLayout.WithFooter(grid, buttons);

        SqlServerActs.ActDraftEdit? edit = null;
        string message = "";
        ok.Click += (_, _) =>
        {
            try
            {
                var (date, from, to2, adjustment) = KsDraftDates.Normalize(dt.Text, fr.Text, to.Text);
                message = adjustment;
                edit = new SqlServerActs.ActDraftEdit(
                    num.Text.Trim().Length == 0 ? details.Number : num.Text.Trim(),
                    date, from, to2, details.Smr, details.Transport, details.Pnr, details.SmrBySystem,
                    dsNo?.Text.Trim() ?? details.DsNo,
                    dsDate?.Text.Trim() ?? details.DsDate,
                    dsExtra?.Text.Trim() ?? details.DsExtra);
                dialog.DialogResult = true;
            }
            catch (FormatException ex)
            {
                MessageBox.Show(dialog, ex.Message, "Дата", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        cancel.Click += (_, _) => dialog.Close();
        return dialog.ShowDialog() == true && edit is not null ? (edit, message) : null;
    }
}
