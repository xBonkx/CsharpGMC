using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleProcurementVisualTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("PeopleGrid")]
    [InlineData("CarsGrid")]
    public void PeopleTablesRemainVirtualizedAndWithoutZebra(string name)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var grid = XDocument.Parse(Source("PeopleWindow.xaml")).Descendants()
            .Single(e => e.Name.LocalName == "DataGrid" && (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("27", (string?)grid.Attribute("MinRowHeight"));
        Assert.Equal("0", (string?)grid.Attribute("AlternationCount"));
        Assert.Equal("True", (string?)grid.Attribute("EnableRowVirtualization"));
    }

    [Fact]
    public void PeopleSearchAndSelectionHandlersRemainConnected()
    {
        var source = Source("PeopleWindow.xaml");
        Assert.Contains("TextChanged=\"Search_Changed\"", source);
        Assert.Contains("Click=\"ClearPeopleSearch_Click\"", source);
        Assert.Contains("CurrentCellChanged=\"CheckCell_Changed\"", source);
        Assert.Contains("SearchBox.Clear();", Source("PeopleSearchUi.cs"));
    }

    [Fact]
    public void ExcelAndOpenIconsUsePythonContours()
    {
        var source = Source("Theme/ToolbarIcons.xaml");
        Assert.Contains("M8.5 8.5l7 7M15.5 8.5l-7 7", source);
        Assert.Contains("M14 4h6v6M20 4l-9 9", source);
        Assert.Contains("IconLabelExcel", Source("PeopleWindow.xaml"));
        Assert.Contains("IconLabelOpen", Source("PeopleWindow.xaml"));
        Assert.Contains("IconLabelExcel", Source("ProcurementRequestDialog.cs"));
    }

    [Theory]
    [InlineData("ProcurementMovementDialog.cs")]
    [InlineData("ProcurementRequestDialog.cs")]
    public void ProcurementPreviewsKeepSharedTableAndValidation(string file)
    {
        var source = Source(file);
        Assert.Contains("ProcurementDialogChrome.Table(_grid)", source);
        Assert.Contains("MinRowHeight = 27", source);
        Assert.Contains("ValidatesOnExceptions = true", source);
        Assert.Contains("TableColumnMenu.SetProtect", source);
        Assert.Contains("grid.EnableRowVirtualization = true", Source("ProcurementDialogChrome.cs"));
    }
}
