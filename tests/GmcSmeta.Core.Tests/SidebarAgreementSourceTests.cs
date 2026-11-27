using System.Xml.Linq;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SidebarAgreementSourceTests
{
    [Theory]
    [InlineData(NavigationMode.Contracts, "Новый договор", "Поиск договора")]
    [InlineData(NavigationMode.Kp, "Новое КП", "Поиск КП")]
    [InlineData(NavigationMode.Archive, "Новый договор", "Поиск в архиве")]
    public void CreationAndSearchCaptionsFollowNavigationMode(NavigationMode mode, string create, string search)
    {
        var captions = NavigationModeCaptions.For(mode);
        Assert.Equal(create, captions.Create);
        Assert.Equal(search, captions.Search);
        if (mode == NavigationMode.Kp)
            Assert.DoesNotContain("договор", captions.EmptyHeading + captions.EmptyHint + captions.CreateHint);
    }

    [Fact]
    public void SidebarSearchHasOneInsetAndNoFixedHeightOrLongPlaceholder()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var markup = XDocument.Load(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var search = markup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "ObjectSearch");
        Assert.Null(search.Attribute("Height"));
        Assert.Null(search.Attribute("Padding"));
        Assert.Equal("{StaticResource SidebarSearch}", (string?)search.Attribute("Style"));
        Assert.Contains("Ctrl+P", (string?)search.Attribute("ToolTip"));
        var style = markup.Descendants().Single(e => (string?)e.Attribute(x + "Key") == "SidebarSearch");
        var host = style.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "PART_ContentHost");
        Assert.Equal("Center", (string?)host.Attribute("VerticalAlignment"));
        Assert.Equal("30,0,32,0", (string?)host.Attribute("Margin"));
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute("Text") == "Поиск договора…");
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "NewObjectButton" &&
            (string?)e.Attribute("Content") == "Новый договор");
        Assert.Contains(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "EmptyNewObjectButton");
        var app = Path.Combine(root.FullName, "src", "GmcSmeta.App");
        var navigation = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("private void RefreshNavigation()\n    {\n        UpdateNavigationCaptions();", navigation.Replace("\r\n", "\n"));
        var captions = File.ReadAllText(Path.Combine(app, "MainWindow.NavigationCaptions.cs"));
        Assert.Contains("NewObjectButton.Content = EmptyNewObjectButton.Content = captions.Create;", captions);
    }
}
