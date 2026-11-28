using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace GmcSmeta.App;

/// <summary>Presentation only: shared form metrics and a scrolling body with independent actions.</summary>
internal static class DynamicFormLayout
{
    internal static void Apply(Window window)
    {
        WorkspaceWindowUi.ApplyChrome(window);
        window.MaxHeight = Math.Max(window.MinHeight, SystemParameters.WorkArea.Height);
        window.MaxWidth = Math.Max(window.MinWidth, SystemParameters.WorkArea.Width);
    }

    internal static void WrapCaption(ContentControl control)
    {
        if (control.Content is not string caption) return;
        AutomationProperties.SetName(control, caption);
        control.Content = new TextBlock { Text = caption, TextWrapping = TextWrapping.Wrap };
    }

    internal static void Field(Control control)
    {
        control.SetResourceReference(FrameworkElement.MinHeightProperty, "LayoutFieldMinHeight");
    }

    internal static DockPanel WithFooter(FrameworkElement body, FrameworkElement footer,
        string marginKey = "LayoutFormDialogMargin")
    {
        body.Margin = new Thickness(0);
        var root = new DockPanel { LastChildFill = true };
        root.SetResourceReference(FrameworkElement.MarginProperty, marginKey);
        footer.SetResourceReference(FrameworkElement.MarginProperty, "LayoutFormFooterGap");
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        return root;
    }

    internal static void StyleActions(Button primary, Button cancel)
    {
        WorkspaceWindowUi.StyleButton(primary, primary: true);
        WorkspaceWindowUi.StyleButton(cancel);
    }
}
