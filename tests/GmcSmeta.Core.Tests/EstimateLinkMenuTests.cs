using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateLinkMenuTests
{
    [Fact]
    public void EstimateActionsReuseTreeLinkWriterWithoutSqlConnectionString()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var panel = Assert.Single(XDocument.Load(Path.Combine(app, "EstimateView.xaml"))
            .Descendants(presentation + "StackPanel")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "ActionsMenuPanel"));
        var button = Assert.Single(panel.Elements(presentation + "Button")
            .Where(e => (string?)e.Attribute("Click") == "SaveCalculationLink_Click"));
        Assert.Equal("Сохранить ссылку на расчёт в папку…", (string?)button.Attribute("Content"));
        var header = File.ReadAllText(Path.Combine(app, "EstimateView.Header.cs"));
        Assert.Contains("main.CreateGmcsLink(_doc);", header);
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("link.Click += (_, _) => CreateGmcsLink(doc);", main);
        var start = main.IndexOf("internal void CreateGmcsLink(DocRow doc)", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = main.IndexOf("File.WriteAllText(path,", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var writer = main[start..end];
        Assert.Contains("db = \"\"", writer);
        Assert.DoesNotContain("_sqlConnectionString", writer);
        Assert.Contains("contract_id = doc.Id", writer);
        Assert.Contains("object_id = doc.ObjectId", writer);
        Assert.Contains("if (dlg.ShowDialog(this) != true) return;", writer);
    }
}
