using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class HeaderAndSidebarCompletionTests
{
    private static readonly XNamespace W = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }
    private static XElement Named(string file, string name) =>
        Assert.Single(XDocument.Parse(Source(file)).Descendants().Where(e => (string?)e.Attribute(X + "Name") == name));

    [Theory]
    [InlineData("ContractCostsTab")]
    [InlineData("PurchaseToggle")]
    public void CalculatorUsesSharedTemplate(string name)
        => Assert.Equal("{StaticResource IconLabelCalculator}", (string?)Named("EstimateView.xaml", name).Attribute("ContentTemplate"));

    [Theory]
    [InlineData("LockChip")]
    [InlineData("CompanyChip")]
    [InlineData("DocumentKindChip")]
    public void HeaderChipsShareSpacingAndSize(string name)
    {
        var chip = Named("EstimateView.xaml", name);
        Assert.Equal("{StaticResource LayoutHeaderChipGap}", (string?)chip.Attribute("Margin"));
        Assert.Equal("{StaticResource LayoutHeaderChipPadding}", (string?)chip.Attribute("Padding"));
        Assert.Equal("{StaticResource LayoutHeaderChipRadius}", (string?)chip.Attribute("CornerRadius"));
        Assert.Equal("{StaticResource LayoutHeaderChipMinHeight}", (string?)chip.Attribute("MinHeight"));
    }

    [Fact]
    public void SidebarActionsUseContentWidthAndCommonHeight()
    {
        var create = Named("MainWindow.xaml", "NewObjectButton");
        var import = Named("MainWindow.xaml", "SidebarImportButton");
        Assert.Same(create.Parent, import.Parent);
        var widths = create.Parent!.Element(W + "Grid.ColumnDefinitions")!.Elements().Select(e => (string?)e.Attribute("Width")).ToArray();
        Assert.Equal(new[] { "*", "6", "Auto" }, widths);
        Assert.Equal((string?)create.Attribute("MinHeight"), (string?)import.Attribute("MinHeight"));
    }

    [Fact]
    public void StartupDoesNotForceBrandFocusOrRemoveTabNavigation()
    {
        var brand = Named("MainWindow.xaml", "SidebarBrandButton");
        Assert.Null(brand.Attribute("Focusable"));
        Assert.Null(brand.Attribute("IsTabStop"));
        Assert.Contains("ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, SidebarBrandButton)", Source("MainWindow.xaml.cs"));
        Assert.Contains("ButtonKeyboardFocusVisual", Source("MainWindow.xaml"));
    }

    [Fact]
    public void ApplicationWindowUsesPackagedIcon()
    {
        Assert.Equal("Assets/app.ico", (string?)XDocument.Parse(Source("MainWindow.xaml")).Root!.Attribute("Icon"));
        Assert.Contains("ApplicationIcon", Source("GmcSmeta.App.csproj"));
    }

    [Fact]
    public void CurrentScaleIsAvailableWithoutStatusStrip()
    {
        Assert.Contains("Размер шрифта ·", Source("MainWindow.Navigation.cs"));
        Assert.Contains("AutomationProperties.SetHelpText(EstimateColumnsButton", Source("EstimateView.TemporaryView.cs"));
        Assert.Contains("Ctrl+0", Source("EstimateView.TemporaryView.cs"));
    }
}
