using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Choose a destination by its actual system ID, never by text or row number.</summary>
public sealed class SectionPickerDialog : Window
{
    private sealed record Choice(long Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ComboBox _sections;

    public long? SelectedSectionId => (_sections.SelectedItem as Choice)?.Id;

    public SectionPickerDialog(IEnumerable<(long Id, string Label)> sections)
    {
        Title = "Перенести позицию в раздел";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        Background = System.Windows.Media.Brushes.White;
        DynamicFormLayout.Apply(this);
        var root = new StackPanel();
        root.Children.Add(new TextBlock { Text = "Куда перенести позицию вместе с комплектующими?",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        _sections = new ComboBox { Padding = new Thickness(7),
            ItemsSource = sections.Select(x => new Choice(x.Id, x.Label)).ToArray(), SelectedIndex = 0 };
        DynamicFormLayout.Field(_sections);
        _sections.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("SelectedItem.Label") { Source = _sections });
        root.Children.Add(_sections);
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0) };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(13, 7, 13, 7) };
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button { Content = "Перенести", Padding = new Thickness(13, 7, 13, 7), Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) => DialogResult = SelectedSectionId is not null;
        bar.Children.Add(cancel);
        bar.Children.Add(ok);
        DynamicFormLayout.StyleActions(ok, cancel);
        Content = DynamicFormLayout.WithFooter(root, bar);
    }
}
