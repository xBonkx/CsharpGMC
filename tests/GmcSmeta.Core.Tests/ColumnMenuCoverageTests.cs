using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

/// <summary>Source-level coverage guard. Runtime and physical mouse acceptance
/// are separate checks; new XAML tables must not silently miss a layout route.</summary>
public sealed class ColumnMenuCoverageTests
{
    [Fact]
    public void EveryDeclaredDataGridHasUniqueProfileOrExplicitSpecialRoute()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace local = "clr-namespace:GmcSmeta.App";
        var profiles = new HashSet<string>(StringComparer.Ordinal);
        var specials = new HashSet<string>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !Path.GetRelativePath(app, f).Split(Path.DirectorySeparatorChar)
                         .Any(p => p is "obj" or "bin")))
        {
            foreach (var grid in XDocument.Load(file).Descendants(presentation + "DataGrid"))
            {
                var name = (string?)grid.Attribute(xaml + "Name") ?? "(unnamed)";
                var key = (string?)grid.Attribute(local + "TableColumnMenu.Key");
                var location = Path.GetFileName(file) + ":" + name;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (!profiles.Add(key)) missing.Add(location + ": duplicate profile " + key);
                }
                else if (location is "EstimateView.xaml:EstimateGrid" or "StagesDialog.xaml:StagesGrid")
                    specials.Add(location);
                else missing.Add(location + ": no column menu route");
            }
        }
        Assert.Empty(missing);
        Assert.Equal(2, specials.Count);
        Assert.NotEmpty(profiles);
        Assert.Contains("TableColumnMenu.InstallHeaderMenu(EstimateGrid", File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs")));
        Assert.Contains("TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu)", File.ReadAllText(Path.Combine(app, "StagesDialog.xaml.cs")));
    }
}
