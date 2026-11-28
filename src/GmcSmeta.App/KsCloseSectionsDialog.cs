using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core;

namespace GmcSmeta.App;

/// <summary>Selection only; the editor retains the write and act lifecycle.</summary>
internal static class KsCloseSectionsDialog
{
    internal static HashSet<long>? Show(FrameworkElement owner, string actNumber,
        IEnumerable<(long SystemId, string Label, decimal Rest)> sections)
    {
        var dialog = new Window
        {
            Title = "Закрыть разделы",
            Width = 560, Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(owner),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Отмеченные разделы попадут в КС-2 №{actNumber} всем остатком (100 %). " +
                   "Снимите галочки с тех, что не закрываем.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        });
        var checks = new Dictionary<long, CheckBox>();
        var list = new StackPanel { Margin = new Thickness(0, 12, 0, 12) };
        foreach (var top in sections)
        {
            var rest = top.Rest;
            var text = rest > 0.005m
                ? $"{top.Label}   —   остаток {Money.Format(rest)} ₽"
                : $"{top.Label}   —   уже сдан";
            var check = new CheckBox { Content = text, IsChecked = rest > 0.005m, Margin = new Thickness(0, 0, 0, 4) };
            checks[top.SystemId] = check;
            list.Children.Add(check);
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 12) };
        Button MarkAll(string caption, bool value)
        {
            var button = new Button
            {
                Content = caption,
                Style = (Style)owner.FindResource("PythonToolbarButton"),
                Margin = new Thickness(0, 0, 8, 0),
            };
            button.Click += (_, _) =>
            {
                foreach (var c in checks.Values) c.IsChecked = value;
            };
            return button;
        }
        row.Children.Add(MarkAll("Отметить все", true));
        row.Children.Add(MarkAll("Снять все", false));
        panel.Children.Add(row);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var ok = new Button
        {
            Content = "Сохранить",
            Style = (Style)owner.FindResource("PythonToolbarPrimary"),
            Padding = new Thickness(14, 5, 14, 5),
        };
        var cancel = new Button { Content = "Отмена", Style = (Style)owner.FindResource("PythonToolbarButton") };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        bool? accepted = null;
        ok.Click += (_, _) => { accepted = true; dialog.DialogResult = true; };
        cancel.Click += (_, _) => { accepted = false; dialog.DialogResult = false; };
        dialog.ShowDialog();
        return accepted == true
            ? checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet()
            : null;
    }
}
