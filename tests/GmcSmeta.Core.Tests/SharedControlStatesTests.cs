using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedControlStatesTests
{
    private static XDocument Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Style(string file, string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return Source(file).Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute(x + "Key") == key);
    }

    [Theory]
    [InlineData("App.xaml", "Segment")]
    [InlineData("App.xaml", "ObjectTab")]
    [InlineData("App.xaml", "ObjectSubTab")]
    [InlineData("App.xaml", "PythonToolToggle")]
    [InlineData("App.xaml", "PythonFilterRadio")]
    [InlineData("App.xaml", "MatFilterToggleWarm")]
    [InlineData("App.xaml", "PythonSecondaryTab")]
    [InlineData("MainWindow.xaml", "SidebarMenuHeader")]
    public void KeyboardFocusUsesAdornerRatherThanPersistentBorder(string file, string key)
    {
        var style = Style(file, key);
        Assert.Contains(style.Elements(), e => (string?)e.Attribute("Property") == "FocusVisualStyle" &&
            (string?)e.Attribute("Value") == "{StaticResource ButtonKeyboardFocusVisual}");
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "Trigger" &&
            (string?)e.Attribute("Property") == "IsKeyboardFocused");
    }

    [Theory]
    [InlineData("EstimateView.xaml", "DocumentSplitLeft")]
    [InlineData("EstimateView.xaml", "DocumentSplitRight")]
    [InlineData("ShipmentsPane.xaml", "ShipmentExportButton")]
    public void ActionChromeDoesNotPaintMouseFocus(string file, string key)
    {
        var style = Style(file, key);
        Assert.DoesNotContain(style.Descendants(), e => e.Name.LocalName == "Trigger" &&
            (string?)e.Attribute("Property") == "IsKeyboardFocused");
        Assert.Contains(style.Descendants(), e => e.Name.LocalName == "Trigger" &&
            (string?)e.Attribute("Property") == "IsPressed");
    }

    [Fact]
    public void TextSelectionAndCellSelectionHaveSeparatePalettes()
    {
        var app = Source("App.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var textBox = app.Descendants().Single(e => e.Name.LocalName == "Style" &&
            (string?)e.Attribute("TargetType") == "TextBox" && e.Attribute(x + "Key") is null);
        Assert.Contains(textBox.Elements(), e => (string?)e.Attribute("Property") == "SelectionTextBrush" &&
            (string?)e.Attribute("Value") == "White");
        Assert.Contains(textBox.Elements(), e => (string?)e.Attribute("Property") == "SelectionBrush" &&
            (string?)e.Attribute("Value") == "#2563EB");
        // Full opacity in legacy adorner mode hides the text underneath.
        // Leave opacity to WPF and explicitly select the glyph-aware renderer at host startup.
        Assert.DoesNotContain(app.Descendants(), e => e.Name.LocalName == "Setter" &&
            (string?)e.Attribute("Property") == "SelectionOpacity");
        var project = Source("GmcSmeta.App.csproj");
        var selectionSwitch = Assert.Single(project.Descendants().Where(e =>
            e.Name.LocalName == "RuntimeHostConfigurationOption" &&
            (string?)e.Attribute("Include") == "Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering"));
        Assert.Equal("false", (string?)selectionSwitch.Attribute("Value"));
        var password = app.Descendants().Single(e => e.Name.LocalName == "Style" &&
            (string?)e.Attribute("TargetType") == "PasswordBox" && e.Attribute(x + "Key") is null);
        Assert.Contains(password.Elements(), e => (string?)e.Attribute("Property") == "SelectionTextBrush" &&
            (string?)e.Attribute("Value") == "White");
        Assert.Equal("#C9DBFB", (string?)app.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "GridSelectionBrush").Attribute("Color"));
        Assert.Equal("#0F172A", (string?)app.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "GridSelectionTextBrush").Attribute("Color"));
    }
}
