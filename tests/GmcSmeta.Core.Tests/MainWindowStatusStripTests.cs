namespace GmcSmeta.Core.Tests;

public sealed class MainWindowStatusStripTests
{
    [Fact]
    public void StatusValuesDoNotReserveWorkspaceHeight()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "MainWindow.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var document = System.Xml.Linq.XDocument.Load(Path.Combine(app, "MainWindow.xaml"));
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var name in new[] { "StatusText", "DatabaseLabel" })
        {
            var value = document.Descendants().Single(e => (string?)e.Attribute(ns + "Name") == name);
            Assert.Equal("Collapsed", (string?)value.Parent!.Attribute("Visibility"));
            Assert.Null(value.Parent.Attribute("DockPanel.Dock"));
        }
        var navigation = File.ReadAllText(Path.Combine(app, "MainWindow.Navigation.cs"));
        Assert.Contains("Сведения о подключении SQL Server", navigation);
        Assert.Contains("Последнее сообщение программы", navigation);
    }
}
