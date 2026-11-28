using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
        ResizeMode = ResizeMode.CanResize;
        MinWidth = 300;
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
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        footer.SetResourceReference(Border.PaddingProperty, "LayoutPromptFooterPadding");
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Отмена", IsCancel = true, TabIndex = 2 };
        cancel.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogActionGap");
        cancel.SetResourceReference(StyleProperty, "PythonToolbarButton");
        cancel.Click += (_, _) => DialogResult = false;
        var ok = new Button
        {
            Content = "Сохранить", IsDefault = true, TabIndex = 1
        };
        ok.SetResourceReference(StyleProperty, "PythonToolbarPrimary");
        ok.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(ok);
        footer.Child = actions;
        root.Children.Add(footer);

        var form = new StackPanel();
        form.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormDialogMargin");
        var heading = new TextBlock
        {
            Text = title
        };
        heading.SetResourceReference(StyleProperty, "UiHeadingText");
        heading.SetResourceReference(FrameworkElement.MarginProperty, "LayoutDialogIntroGap");
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiH2Size");
        form.Children.Add(heading);
        var caption = DynamicFormLayout.Secondary(label, "LayoutFormGroupHeadingGap");
        form.Children.Add(caption);
        Box = new TextBox
        {
            Text = value,
            TabIndex = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        DynamicFormLayout.Field(Box);
        Box.SetResourceReference(Control.PaddingProperty, "LayoutFieldPadding");
        AutomationProperties.SetName(Box, label);
        // Use the window's default/cancel buttons, including standard keyboard and IME handling.
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
