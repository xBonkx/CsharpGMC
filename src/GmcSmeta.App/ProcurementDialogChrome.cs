using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GmcSmeta.App;

/// <summary>Qt procurement dialog metrics, shared by code-created WPF windows.</summary>
internal static class ProcurementDialogChrome
{
    public static Thickness Outer => new(22, 18, 22, 18);
    public static Thickness RequestOuter => new(22, 18, 22, 16);
    public static Thickness SectionGap => new(0, 0, 0, 10);
    public static Thickness FooterGap => new(0, 10, 0, 0);
    public static void Apply(Window window)
    {
        window.SetResourceReference(Control.FontFamilyProperty, "UiFontFamily");
        window.SetResourceReference(Control.FontSizeProperty, "UiBodySize");
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        window.ShowInTaskbar = false;
        window.MaxWidth = SystemParameters.WorkArea.Width;
        window.MaxHeight = SystemParameters.WorkArea.Height;
        window.Background = Brushes.White;
    }
    public static TextBlock Heading(string text)
    {
        var label = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(FrameworkElement.StyleProperty, "UiHeadingText");
        label.SetResourceReference(TextBlock.FontSizeProperty, "UiH2Size");
        return label;
    }
    public static TextBlock Hint(string text)
    {
        var hint = new TextBlock { Text = text };
        hint.SetResourceReference(FrameworkElement.StyleProperty, "UiSecondaryText");
        return hint;
    }
    public static void Table(DataGrid grid)
    {
        grid.SetResourceReference(FrameworkElement.StyleProperty, "DocumentTable");
        grid.MinRowHeight = 27;
        grid.AlternationCount = 0;
        grid.EnableRowVirtualization = true;
        grid.EnableColumnVirtualization = true;
        VirtualizingPanel.SetVirtualizationMode(grid, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
    }
    public static void Column(DataGridTextColumn column, TextAlignment alignment)
    {
        var prefix = alignment == TextAlignment.Right ? "CompactCostMoney" :
            alignment == TextAlignment.Center ? "CompactCostCenter" : "CompactCostText";
        column.ElementStyle = (Style)Application.Current.FindResource(prefix);
        column.EditingElementStyle = (Style)Application.Current.FindResource(
            alignment == TextAlignment.Right ? "CompactCostMoneyEditor" :
            alignment == TextAlignment.Center ? "CompactCostCenterEditor" : "CompactCostEditor");
        column.HeaderStyle = (Style)Application.Current.FindResource(
            alignment == TextAlignment.Right ? "CompactCostMoneyHeader" :
            alignment == TextAlignment.Center ? "CompactCostHeader" : "CompactCostTextHeader");
    }
    public static void Buttons(Button primary, Button cancel, string primaryStyle = "PythonToolbarPrimary")
    {
        primary.SetResourceReference(FrameworkElement.StyleProperty, primaryStyle);
        cancel.SetResourceReference(FrameworkElement.StyleProperty, "PythonToolbarButton");
        primary.MinHeight = cancel.MinHeight = 30;
        primary.IsDefault = true;
        cancel.IsCancel = true;
    }
}
