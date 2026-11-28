using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class AuxiliaryDesignContinuationTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Theory]
    [InlineData("PtoWindow.xaml", "Search")]
    [InlineData("PtoWindow.xaml", "StatusFilter")]
    [InlineData("MetalWarehouseWindow.xaml", "StockCategory")]
    [InlineData("MetalWarehouseWindow.xaml", "StockSearch")]
    [InlineData("MetalWarehouseWindow.xaml", "HistorySearch")]
    [InlineData("PeopleWindow.xaml", "CompanyBox")]
    public void FontSensitiveControlsHaveNoFixedHeight(string file, string name)
    {
        var element = Assert.Single(XDocument.Parse(Source(file)).Descendants()
            .Where(e => (string?)e.Attribute(X + "Name") == name));
        Assert.Null(element.Attribute("Height"));
    }

    [Theory]
    [InlineData("FileUp_Click", "IconOnlyArrowUp", "Выше")]
    [InlineData("FileDown_Click", "IconOnlyArrowDown", "Ниже")]
    public void LetterMovementUsesVectorWithAccessibleTarget(string handler, string template, string label)
    {
        var button = Assert.Single(XDocument.Parse(Source("LetterEditorWindow.xaml")).Descendants(W + "Button")
            .Where(e => (string?)e.Attribute("Click") == handler));
        Assert.Equal("{StaticResource " + template + "}", (string?)button.Attribute("ContentTemplate"));
        Assert.Equal(label, (string?)button.Attribute("AutomationProperties.Name"));
        Assert.Equal("32", (string?)button.Attribute("Width"));
    }

    [Theory]
    [InlineData("PtoWindow.xaml", "Backup_Click")]
    [InlineData("PtoEditorWindow.xaml", "History_Click")]
    [InlineData("LetterEditorWindow.xaml", "FileAdd_Click")]
    [InlineData("PassportsWindow.xaml", "AddPoint_Click")]
    [InlineData("MetalWarehouseWindow.xaml", "Operation_Click")]
    public void PythonTextOnlyCommandsStayTextOnly(string file, string handler)
    {
        var button = Assert.Single(XDocument.Parse(Source(file)).Descendants(W + "Button")
            .Where(e => (string?)e.Attribute("Click") == handler));
        Assert.Null(button.Attribute("ContentTemplate"));
    }

    [Theory]
    [InlineData("SenderBox")]
    [InlineData("RecipientBox")]
    public void LetterAddressFieldsGrowWithinBounds(string name)
    {
        var field = Assert.Single(XDocument.Parse(Source("LetterEditorWindow.xaml")).Descendants()
            .Where(e => (string?)e.Attribute(X + "Name") == name));
        Assert.Null(field.Attribute("Height"));
        Assert.Equal("110", (string?)field.Attribute("MinHeight"));
        Assert.Equal("240", (string?)field.Attribute("MaxHeight"));
        Assert.Equal("Auto", (string?)field.Attribute("VerticalScrollBarVisibility"));
    }

    [Fact]
    public void StockCategoryAndSearchWrapAsGroups()
    {
        var doc = XDocument.Parse(Source("MetalWarehouseWindow.xaml"));
        var category = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "StockCategory"));
        var search = Assert.Single(doc.Descendants().Where(e => (string?)e.Attribute(X + "Name") == "StockSearch"));
        Assert.Equal(W + "StackPanel", category.Parent!.Name);
        Assert.Equal(W + "WrapPanel", category.Parent.Parent!.Name);
        Assert.Same(category.Parent.Parent, search.Parent!.Parent);
    }

    [Fact]
    public void OriginalAppIconIsUsedForWindowAndExecutable()
    {
        Assert.Equal("Assets/app.ico", (string?)XDocument.Parse(Source("MainWindow.xaml")).Root!.Attribute("Icon"));
        var project = XDocument.Parse(Source("GmcSmeta.App.csproj"));
        Assert.Equal(@"Assets\app.ico", Assert.Single(project.Descendants("ApplicationIcon")).Value);
        Assert.Contains(project.Descendants("Resource"), e => (string?)e.Attribute("Include") == @"Assets\app.ico");
    }
}
