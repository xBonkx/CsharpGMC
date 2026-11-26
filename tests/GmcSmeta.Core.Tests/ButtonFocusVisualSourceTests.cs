using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ButtonFocusVisualSourceTests
{
    [Fact]
    public void MouseFocusedActionButtonsDoNotPaintPersistentFocusBorders()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var shared = XDocument.Load(Path.Combine(app, "App.xaml"));
        var main = XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        XElement Style(XDocument doc, string? key) => doc.Descendants(wpf + "Style")
            .Single(s => (string?)s.Attribute(x + "Key") == key &&
                (string?)s.Attribute("TargetType") == "Button");

        var baseStyle = Style(shared, null);
        var ownFocus = new[] { baseStyle, Style(shared, "PythonTabAction"),
            Style(main, "SidebarIconButton"), Style(main, "SidebarHomeButton"),
            Style(main, "DocumentCardButton"), Style(main, "NewContractCardButton") };
        foreach (var style in ownFocus)
            Assert.Contains(style.Elements(wpf + "Setter"), s =>
                (string?)s.Attribute("Property") == "FocusVisualStyle" &&
                (string?)s.Attribute("Value") == "{StaticResource ButtonKeyboardFocusVisual}");

        var derived = new[] { "PrimaryButton", "PythonToolbarPrimary", "ProcurementRequestPrimary", "DangerButton" }
            .Select(k => Style(shared, k));
        foreach (var style in ownFocus.Concat(derived))
        {
            Assert.DoesNotContain(style.Descendants(wpf + "Trigger"), t =>
                (string?)t.Attribute("Property") == "IsKeyboardFocused");
            Assert.Contains(style.Descendants(wpf + "Trigger"), t =>
                (string?)t.Attribute("Property") == "IsMouseOver");
        }
        Assert.Contains(shared.Descendants(wpf + "Style"), s =>
            (string?)s.Attribute(x + "Key") == "ButtonKeyboardFocusVisual");
    }
}
