using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class SharedFieldAndMenuSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument Load(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    private static XElement Style(XDocument doc, string type, string? key = null) =>
        Assert.Single(doc.Descendants(W + "Style").Where(s =>
            (string?)s.Attribute("TargetType") == type && (string?)s.Attribute(X + "Key") == key));

    private static string? Value(XElement e, string property) =>
        (string?)Assert.Single(e.Elements(W + "Setter").Where(s =>
            (string?)s.Attribute("Property") == property)).Attribute("Value");

    private static XElement Named(XElement e, string name) =>
        Assert.Single(e.Descendants().Where(n => (string?)n.Attribute(X + "Name") == name));

    [Theory]
    [InlineData("PasswordBox", null)]
    [InlineData("ComboBox", null)]
    [InlineData("TextBox", null)]
    [InlineData("TextBox", "PythonSearch")]
    public void FieldsSharePythonInset(string type, string? key)
    {
        var metrics = Load("Theme/LayoutMetrics.xaml");
        Assert.Equal("10,6", metrics.Descendants().Single(e =>
            (string?)e.Attribute(X + "Key") == "LayoutFieldPadding").Value);
        var style = Style(Load("App.xaml"), type, key);
        Assert.Equal("{StaticResource LayoutFieldPadding}", Value(style, "Padding"));
        if (type != "TextBox" || key != null)
            Assert.Equal("{StaticResource LayoutFieldMinHeight}", Value(style, "MinHeight"));
    }

    [Fact]
    public void ComboKeepsEditablePartAndAlignedPopup()
    {
        var app = Load("App.xaml");
        var style = Style(app, "ComboBox");
        var metrics = Load("Theme/LayoutMetrics.xaml");
        Assert.Equal("22", metrics.Descendants().Single(e =>
            (string?)e.Attribute(X + "Key") == "LayoutComboArrowWidth").Value);
        Assert.Equal("True", (string?)Named(style, "SelectedContent").Attribute("ClipToBounds"));
        var editor = Named(style, "PART_EditableTextBox");
        Assert.Equal("{TemplateBinding IsReadOnly}", (string?)editor.Attribute("IsReadOnly"));
        var popup = Named(style, "PART_Popup");
        Assert.Equal("{TemplateBinding IsDropDownOpen}", (string?)popup.Attribute("IsOpen"));
        Assert.Equal("-4", (string?)popup.Attribute("HorizontalOffset"));
        Assert.Contains(style.Descendants(W + "Setter"), s =>
            (string?)s.Attribute("TargetName") == "ToggleButton" &&
            (string?)s.Attribute("Property") == "Width" &&
            (string?)s.Attribute("Value") == "{StaticResource LayoutComboArrowWidth}");
        var item = Style(app, "ComboBoxItem");
        var disabled = Assert.Single(item.Descendants(W + "Trigger").Where(t =>
            (string?)t.Attribute("Property") == "IsEnabled" && (string?)t.Attribute("Value") == "False"));
        Assert.Equal("{StaticResource UiDisabledTextBrush}", Value(disabled, "Foreground"));
    }

    [Theory]
    [InlineData("IsHighlighted", "True", "#1E40AF")]
    [InlineData("IsSubmenuOpen", "True", "#1E40AF")]
    [InlineData("IsEnabled", "False", "{StaticResource UiDisabledTextBrush}")]
    public void MenuHeaderAndShortcutFollowSameState(string property, string state, string color)
    {
        var style = Style(Load("App.xaml"), "MenuItem");
        var trigger = Assert.Single(style.Descendants(W + "Trigger").Where(t =>
            (string?)t.Attribute("Property") == property && (string?)t.Attribute("Value") == state));
        foreach (var target in new[] { "HeaderHost", "ShortcutText" })
            Assert.Contains(trigger.Elements(W + "Setter"), s =>
                (string?)s.Attribute("TargetName") == target &&
                (string?)s.Attribute("Value") == color);
        Assert.NotNull(Named(style, "PART_Popup"));
        Assert.NotNull(Named(style, "CheckGlyph"));
    }
}
