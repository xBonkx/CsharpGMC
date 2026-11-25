using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

/// <summary>CTX-020 source guard; does not replace Windows dialog acceptance.</summary>
public sealed class DocumentLinkMenuSourceTests
{
    [Fact]
    public void DocumentActionsReuseNavigationLinkWriterWithoutConnectionString()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = XDocument.Load(Path.Combine(app, "EstimateView.xaml"))
            .Descendants(wpf + "StackPanel").Single(e => (string?)e.Attribute(xaml + "Name") == "ActionsMenuPanel");
        var command = Assert.Single(panel.Elements(wpf + "Button")
            .Where(e => (string?)e.Attribute("Click") == "SaveDocumentLink_Click"));
        Assert.Equal("Сохранить ссылку на расчёт в папку…", (string?)command.Attribute("Content"));
        var header = File.ReadAllText(Path.Combine(app, "EstimateView.Header.cs"));
        Assert.Contains("main.CreateGmcsLink(_doc);", header);
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("link.Click += (_, _) => CreateGmcsLink(doc);", main);
        Assert.Contains("internal void CreateGmcsLink(DocRow doc)", main);
        Assert.Contains("db = \"\",", main);
        Assert.DoesNotContain("db = _sqlConnectionString", main);
    }
}
