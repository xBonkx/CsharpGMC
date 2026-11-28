using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DisabledTextContrastSourceTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string AppDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    [Fact]
    public void DisabledXamlTriggersNeverFadeTheEntireControl()
    {
        var app = AppDirectory();
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")))
        {
            var doc = XDocument.Load(file);
            foreach (var trigger in doc.Descendants().Where(e => e.Name == W + "Trigger" || e.Name == W + "MultiTrigger"))
            {
                bool Disabled(XElement e) => (string?)e.Attribute("Property") == "IsEnabled" &&
                    string.Equals((string?)e.Attribute("Value"), "False", StringComparison.OrdinalIgnoreCase);
                if (!Disabled(trigger) && !trigger.Descendants(W + "Condition").Any(Disabled)) continue;
                foreach (var setter in trigger.Elements(W + "Setter"))
                {
                    if ((string?)setter.Attribute("Property") != "Opacity" || setter.Attribute("TargetName") is not null)
                        continue;
                    if (double.TryParse((string?)setter.Attribute("Value"), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var opacity) && opacity < 1)
                        violations.Add(Path.GetRelativePath(app, file) + ": " + setter);
                }
            }
        }
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Theory]
    [InlineData("Segment", "SegmentBorder")]
    [InlineData("ObjectTab", "TabBorder")]
    [InlineData("ObjectSubTab", "SubTabBorder")]
    [InlineData("PythonFilterChip", "FilterChrome")]
    [InlineData("PythonFilterRadio", "FilterSurface")]
    public void SelectedDisabledControlRetainsDistinctReadableBackground(string key, string chrome)
    {
        var doc = XDocument.Load(Path.Combine(AppDirectory(), "App.xaml"));
        var style = Assert.Single(doc.Descendants(W + "Style").Where(e => (string?)e.Attribute(X + "Key") == key));
        var disabled = Assert.Single(style.Descendants(W + "Trigger").Where(e =>
            (string?)e.Attribute("Property") == "IsEnabled" && (string?)e.Attribute("Value") == "False"));
        Assert.Contains(disabled.Elements(W + "Setter"), e => (string?)e.Attribute("Property") == "Foreground" &&
            (string?)e.Attribute("Value") == "{StaticResource UiDisabledTextBrush}");
        var both = Assert.Single(style.Descendants(W + "MultiTrigger").Where(e =>
            e.Descendants(W + "Condition").Any(c => (string?)c.Attribute("Property") == "IsEnabled" &&
                (string?)c.Attribute("Value") == "False") &&
            e.Descendants(W + "Condition").Any(c => (string?)c.Attribute("Property") == "IsChecked" &&
                (string?)c.Attribute("Value") == "True")));
        Assert.Contains(both.Elements(W + "Setter"), e => (string?)e.Attribute("TargetName") == chrome &&
            (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "#E8F0FE");
    }
    [Theory]
    [InlineData("PrimaryButton", "PrimaryBorder")]
    [InlineData("PythonToolbarPrimary", "PrimaryChrome")]
    [InlineData("ProcurementRequestPrimary", "Chrome")]
    [InlineData("DangerButton", "DangerSurface")]
    public void DisabledPrimaryActionUsesNeutralReadablePalette(string key, string chrome)
    {
        var doc = XDocument.Load(Path.Combine(AppDirectory(), "App.xaml"));
        var style = Assert.Single(doc.Descendants(W + "Style").Where(e => (string?)e.Attribute(X + "Key") == key));
        var disabled = Assert.Single(style.Descendants(W + "Trigger").Where(e =>
            (string?)e.Attribute("Property") == "IsEnabled" && (string?)e.Attribute("Value") == "False"));
        Assert.Contains(disabled.Elements(W + "Setter"), e => (string?)e.Attribute("Property") == "Foreground" &&
            (string?)e.Attribute("Value") == "{StaticResource UiDisabledTextBrush}");
        Assert.Contains(disabled.Elements(W + "Setter"), e => (string?)e.Attribute("TargetName") == chrome &&
            (string?)e.Attribute("Property") == "Background" && (string?)e.Attribute("Value") == "#F7F9FC");
    }

}
