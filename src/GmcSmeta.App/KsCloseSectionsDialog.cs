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
        DynamicFormLayout.Apply(dialog);
        var panel = new StackPanel();
        panel.Children.Add(DynamicFormLayout.Secondary(
            $"Отмеченные разделы попадут в КС-2 №{actNumber} всем остатком (100 %). " +
            "Снимите галочки с тех, что не закрываем."));
        var checks = new Dictionary<long, CheckBox>();
        var list = new StackPanel();
        list.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogListGap");
        foreach (var top in sections)
        {
            var rest = top.Rest;
            var text = rest > 0.005m
                ? $"{top.Label}   —   остаток {Money.Format(rest)} ₽"
                : $"{top.Label}   —   уже сдан";
            var check = new CheckBox { Content = text, IsChecked = rest > 0.005m };
            check.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogListItemGap");
            DynamicFormLayout.WrapCaption(check);
            checks[top.SystemId] = check;
            list.Children.Add(check);
        }
        panel.Children.Add(new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 320,
        });
        var row = new WrapPanel { Orientation = Orientation.Horizontal };
        row.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionRowGap");
        Button MarkAll(string caption, bool value)
        {
            var button = new Button
            {
                Content = caption,
                Style = (Style)owner.FindResource("PythonToolbarButton"),
            };
            button.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
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
            IsDefault = true,
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true, Style = (Style)owner.FindResource("PythonToolbarButton") };
        ok.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dialog.Content = DynamicFormLayout.WithFooter(panel, buttons);
        bool? accepted = null;
        ok.Click += (_, _) => { accepted = true; dialog.DialogResult = true; };
        cancel.Click += (_, _) => { accepted = false; dialog.DialogResult = false; };
        dialog.ShowDialog();
        return accepted == true
            ? checks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToHashSet()
            : null;
    }
}
