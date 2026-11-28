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
        var menu = File.ReadAllText(Path.Combine(app, "EstimateView.DocumentActions.cs"));
        Assert.Contains("Add(\"Сохранить ссылку на расчёт в папку…\", SaveDocumentLink_Click, \"Folder\", Settled);", menu);
        Assert.True(menu.IndexOf("ShowHistory_Click", StringComparison.Ordinal) <
            menu.IndexOf("SaveDocumentLink_Click", StringComparison.Ordinal));
        var header = File.ReadAllText(Path.Combine(app, "EstimateView.Header.cs"));
        Assert.Contains("main.CreateGmcsLink(_doc);", header);
        var main = File.ReadAllText(Path.Combine(app, "MainWindow.xaml.cs"));
        Assert.Contains("link.Click += (_, _) => RunDocumentCommand(() => CreateGmcsLink(doc));", main);
        var links = File.ReadAllText(Path.Combine(app, "MainWindow.DocumentLinks.cs"));
        var codec = File.ReadAllText(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Domain", "GmcsDocumentLink.cs"));
        Assert.Contains("internal void CreateGmcsLink(DocRow doc)", links);
        Assert.Contains("GmcsDocumentLink.Serialize(doc,", links);
        Assert.Contains("db = \"\",", codec);
        Assert.DoesNotContain("_sqlConnectionString", links);
        Assert.DoesNotContain("_sqlConnectionString", codec);
    }
}
