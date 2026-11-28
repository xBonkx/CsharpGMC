using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Shared single-field prompt. The caller retains validation and empty-value semantics.</summary>
public sealed class PromptDialog : Window
{
    public string Value => Box.Text.Trim();
    private readonly TextBox Box;

    public PromptDialog(string title, string label, string value)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        Background = Brushes.White;
        SetResourceReference(FontFamilyProperty, "UiFontFamily");
        SetResourceReference(FontSizeProperty, "UiBodySize");
        Foreground = Brush("#0F172A");
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var root = new DockPanel();
        var footer = new Border
        {
            Background = Brush("#F8FAFC"), BorderBrush = Brush("#E4E8EF"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12)
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Отмена", MinWidth = 94, IsCancel = true };
        cancel.SetResourceReference(StyleProperty, "PythonToolbarButton");
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button
        {
            Content = "Сохранить", MinWidth = 108, Margin = new Thickness(8, 0, 0, 0), IsDefault = true
        };
        ok.SetResourceReference(StyleProperty, "PythonToolbarPrimary");
        ok.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        footer.Child = actions;
        root.Children.Add(footer);

        var form = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };
        var heading = new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        };
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiH2Size");
        form.Children.Add(heading);
        var caption = new TextBlock
        {
            Text = label, Foreground = Brush("#475569"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6)
        };
        form.Children.Add(caption);
        Box = new TextBox
        {
            Text = value, MinHeight = 34, Padding = new Thickness(10, 4, 10, 4),
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        AutomationProperties.SetName(Box, label);
        Box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DialogResult = true; e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };
        form.Children.Add(Box);
        root.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        DynamicFormLayout.Apply(this);
        Content = root;
        Loaded += (_, _) => { Box.Focus(); Box.SelectAll(); };
    }

    private static Brush Brush(string hex) =>
        (Brush)new BrushConverter().ConvertFromString(hex)!;
}
