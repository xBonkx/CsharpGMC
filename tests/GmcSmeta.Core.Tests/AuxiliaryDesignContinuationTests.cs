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
    [InlineData("PtoWindow.xaml", "Backup_Click", "IconLabelDownload")]
    [InlineData("PtoWindow.xaml", "Restore_Click", "IconLabelUpload")]
    [InlineData("PtoEditorWindow.xaml", "History_Click", "IconLabelHistory")]
    [InlineData("PtoEditorWindow.xaml", "FileProperties_Click", "IconLabelSettings")]
    [InlineData("LetterEditorWindow.xaml", "FileAdd_Click", "IconLabelClip")]
    [InlineData("LetterEditorWindow.xaml", "FileUp_Click", "IconOnlyArrowUp")]
    [InlineData("LetterEditorWindow.xaml", "FileDown_Click", "IconOnlyArrowDown")]
    [InlineData("PassportsWindow.xaml", "AddPoint_Click", "IconLabelPlus")]
    [InlineData("PassportsWindow.xaml", "DeletePoint_Click", "IconLabelTrash")]
    [InlineData("MetalWarehouseWindow.xaml", "Operation_Click", "IconLabelFilePlus")]
    [InlineData("MetalWarehouseWindow.xaml", "LotPay_Click", "IconLabelCheck")]
    public void ToolbarIconDoesNotReplaceCommand(string file, string handler, string template)
    {
        var button = Assert.Single(XDocument.Parse(Source(file)).Descendants(W + "Button")
            .Where(e => (string?)e.Attribute("Click") == handler));
        Assert.Equal("{StaticResource " + template + "}", (string?)button.Attribute("ContentTemplate"));
        var icons = XDocument.Parse(Source("Theme/ToolbarIcons.xaml"));
        Assert.Single(icons.Descendants().Where(e => (string?)e.Attribute(X + "Key") == template));
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
