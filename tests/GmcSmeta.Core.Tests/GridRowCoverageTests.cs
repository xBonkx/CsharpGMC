using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class GridRowCoverageTests
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
    public void EveryDeclaredGridRowHasContentOrAnExplicitLayoutPurpose()
    {
        var app = AppDirectory();
        var files = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "obj" or "bin")).ToArray();
        Assert.True(files.Length >= 40);
        var intentional = new HashSet<(string File, string Grid, int Row, string Height)>
        {
            ("MainWindow.xaml", "EmptyPage", 0, "2*"),
            ("MainWindow.xaml", "EmptyPage", 2, "3*"),
            ("ProcurementPane.xaml", "ProcurementFilterLayout", 1, "Auto")
        };
        var unexplained = new List<string>();
        foreach (var file in files)
        {
            var doc = XDocument.Load(file);
            foreach (var grid in doc.Descendants(W + "Grid"))
            {
                var rows = grid.Element(W + "Grid.RowDefinitions")?.Elements(W + "RowDefinition").ToArray();
                if (rows is null || rows.Length == 0) continue;
                var occupied = new HashSet<int>();
                foreach (var child in grid.Elements().Where(e => !e.Name.LocalName.Contains('.')))
                {
                    Assert.True(int.TryParse((string?)child.Attribute("Grid.Row") ?? "0", out var row),
                        "Dynamic attached row requires an explicit layout review: " + file);
                    Assert.True(int.TryParse((string?)child.Attribute("Grid.RowSpan") ?? "1", out var span),
                        "Dynamic row span requires an explicit layout review: " + file);
                    Assert.True(row >= 0 && row < rows.Length && span > 0 && row + span <= rows.Length,
                        "Grid row/span outside declared rows: " + file);
                    for (int i = row; i < row + span; i++) occupied.Add(i);
                }
                var name = (string?)grid.Attribute(X + "Name") ?? "";
                for (int i = 0; i < rows.Length; i++)
                {
                    if (occupied.Contains(i)) continue;
                    var key = (Path.GetRelativePath(app, file).Replace('\\', '/'), name, i,
                        (string?)rows[i].Attribute("Height") ?? "*");
                    if (!intentional.Remove(key)) unexplained.Add(key.ToString());
                }
            }
        }
        Assert.True(unexplained.Count == 0, string.Join(Environment.NewLine, unexplained));
        Assert.Empty(intentional); // remove exceptions when their corresponding layout changes
        var responsive = File.ReadAllText(Path.Combine(app, "ProcurementPane.Navigation.cs"));
        Assert.Contains("Grid.SetRow(ProcurementFilterTail, stacked ? 1 : 0)", responsive);
    }

    [Theory]
    [InlineData("MetalDialogs.cs", "DynamicFormLayout.WithFooter(_grid, actions)")]
    [InlineData("KsActPropertiesDialog.cs", "DynamicFormLayout.WithFooter(grid, buttons)")]
    public void CodeCreatedFormsKeepActionsOutsideGridRows(string file, string footer)
    {
        var source = File.ReadAllText(Path.Combine(AppDirectory(), file));
        Assert.Contains(footer, source);
        Assert.Contains("new WrapPanel", source);
        Assert.Contains("LayoutFormLabelMargin", source);
        Assert.Contains("LayoutFormFieldMargin", source);
        Assert.Contains("AutomationProperties.SetName", source);
    }
}
