using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class NameWrapPreferenceSourceTests
{
    private static string AppDirectory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "NameWrapPreference.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        return Path.Combine(root!.FullName, "src", "GmcSmeta.App");
    }

    [Theory]
    [InlineData("EstimateView.TemporaryView.cs", "GroupMenu_Click")]
    [InlineData("EstimateView.Materials.cs", "MaterialsGrouping_Click")]
    [InlineData("ProcurementPane.Navigation.cs", "ProcurementGroupMenu_Click")]
    public void GroupingMenuUsesSharedPreference(string path, string handler)
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), path));
        var start = code.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = code.IndexOf("menu.IsOpen = true;", start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.Contains("NameWrapPreference.AddMenuItem(menu);", code[start..end]);
    }

    [Theory]
    [InlineData("EstimateView.xaml", 1)]
    [InlineData("EstimateMaterialsPanel.xaml", 1)]
    [InlineData("ProcurementPane.xaml", 1)]
    public void NameDisplayHasSharedWrapAndEllipsis(string path, int expectedNames)
    {
        var xaml = XDocument.Load(Path.Combine(AppDirectory(), path));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var trigger = Assert.Single(xaml.Descendants(ns + "DataTrigger").Where(t =>
            (string?)t.Attribute("Binding") == "{Binding IsEnabled, Source={x:Static local:NameWrapPreference.Current}}"));
        Assert.Equal("False", (string?)trigger.Attribute("Value"));
        Assert.Contains(trigger.Elements(ns + "Setter"), s =>
            (string?)s.Attribute("Property") == "TextWrapping" && (string?)s.Attribute("Value") == "NoWrap");
        Assert.Contains(trigger.Elements(ns + "Setter"), s =>
            (string?)s.Attribute("Property") == "TextTrimming" && (string?)s.Attribute("Value") == "CharacterEllipsis");
        var style = trigger.Parent!.Parent!;
        Assert.Contains(style.Elements(ns + "Setter"), s =>
            (string?)s.Attribute("Property") == "TextWrapping" && (string?)s.Attribute("Value") == "Wrap");
        Assert.Contains(style.Elements(ns + "Setter"), s =>
            (string?)s.Attribute("Property") == "Text" && (string?)s.Attribute("Value") == "{Binding Name}");
        Assert.Contains(trigger.Elements(ns + "Setter"), s =>
            (string?)s.Attribute("Property") == "Text" &&
            (string?)s.Attribute("Value") == "{Binding Name, Converter={StaticResource SingleLineName}}");
        var nameBlocks = xaml.Descendants(ns + "TextBlock").Where(t =>
            (string?)t.Attribute("Style") == "{StaticResource DocumentNameDisplay}" || t.Descendants(ns + "DataTrigger").Contains(trigger)).ToArray();
        Assert.Equal(expectedNames, nameBlocks.Length);
        // A local Text binding would override both style setters.
        Assert.All(nameBlocks, t => Assert.Null(t.Attribute("Text")));
    }

    [Theory]
    [InlineData("EstimateView.xaml", "EstimateGrid")]
    [InlineData("EstimateMaterialsPanel.xaml", "MaterialsGrid")]
    [InlineData("ProcurementPane.xaml", "BuyGrid")]
    public void CompactModeConstrictsEveryRowAndWrapModeRestoresAutoHeight(string path, string gridName)
    {
        var xaml = XDocument.Load(Path.Combine(AppDirectory(), path));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = Assert.Single(xaml.Descendants(ns + "DataGrid")
            .Where(e => (string?)e.Attribute(x + "Name") == gridName));
        Assert.Equal("27", (string?)grid.Attribute("MinRowHeight"));
        Assert.Equal("{Binding RowHeight, Source={x:Static local:NameWrapPreference.Current}}",
            (string?)grid.Attribute("RowHeight"));
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "NameWrapPreference.cs"));
        Assert.Contains("IsEnabled ? double.NaN : 27d", code);
        Assert.Contains("PropertyChangedEventArgs(nameof(RowHeight))", code);
        Assert.Contains("NameDisplayText.SingleLine(value as string)", code);
    }

    [Fact]
    public void PreferenceIsLocalAndRetainsLegacyDefault()
    {
        var code = File.ReadAllText(Path.Combine(AppDirectory(), "NameWrapPreference.cs"));
        Assert.Contains("В несколько строк (строка становится выше)", code);
        Assert.Contains("return EstimateUiSettings.Load().WrapNames;", code);
        Assert.Contains("PropertyChanged?.Invoke", code);
        Assert.Contains("File.Move(temporary, FilePath, overwrite: true);", code);
        Assert.DoesNotContain("SqlServer", code);
    }
}
