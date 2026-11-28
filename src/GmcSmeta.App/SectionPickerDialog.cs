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
        root.Children.Add(DynamicFormLayout.Secondary("Куда перенести позицию вместе с комплектующими?", "LayoutDialogIntroGap"));
        _sections = new ComboBox {
            ItemsSource = sections.Select(x => new Choice(x.Id, x.Label)).ToArray(), SelectedIndex = -1, TabIndex = 0 };
        DynamicFormLayout.Field(_sections);
        System.Windows.Automation.AutomationProperties.SetName(_sections, "Раздел назначения");
        _sections.SetBinding(ToolTipProperty, new System.Windows.Data.Binding("SelectedItem.Label") { Source = _sections });
        root.Children.Add(_sections);
        var bar = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Отмена", IsCancel = true, TabIndex = 2 };
        cancel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        var ok = new Button { Content = "Перенести", IsEnabled = false, TabIndex = 1 };
        _sections.SelectionChanged += (_, _) => ok.IsEnabled = SelectedSectionId is not null;
        ok.Click += (_, _) =>
        {
            if (SelectedSectionId is not null) DialogResult = true;
        };
        bar.Children.Add(cancel);
        bar.Children.Add(ok);
        DynamicFormLayout.StyleActions(ok, cancel);
        Content = DynamicFormLayout.WithFooter(root, bar);
        Loaded += (_, _) => _sections.Focus();
    }
}
