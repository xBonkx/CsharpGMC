using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class DirectorFooterSourceTests
{
    [Fact]
    public void DirectorFooterKeepsHintAndCollapsesEmptyStatusAfterSuccessfulLoad()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xml = XDocument.Load(Path.Combine(app, "DirectorView.xaml"));
        var panel = Assert.Single(xml.Descendants(presentation + "DockPanel")
            .Where(e => (string?)e.Attribute(xaml + "Name") == "LoadStatusPanel"));
        Assert.Equal("Collapsed", (string?)panel.Attribute("Visibility"));
        Assert.Equal("0,12,0,0", (string?)panel.Parent!.Attribute("Margin"));
        Assert.Contains(panel.Parent.Elements(presentation + "TextBlock"),
            e => ((string?)e.Attribute("Text"))?.Contains("Суммы с НДС.") == true);
        var code = File.ReadAllText(Path.Combine(app, "DirectorView.xaml.cs"));
        Assert.Contains("LoadStatusPanel.Visibility = Visibility.Visible;", code);
        Assert.Contains("LoadStatusPanel.Visibility = Visibility.Collapsed;", code);
        Assert.Contains("RetryButton.Visibility = Visibility.Visible;", code);
        Assert.Contains("e.ChangedButton != MouseButton.Left", code);
        Assert.Contains("ItemsControl.ContainerFromElement(DirectorGrid, source) is not DataGridRow container", code);
        Assert.Contains("container.Item is not DirectorPreviewRow row", code);
        Assert.DoesNotContain("DirectorGrid.SelectedItem is DirectorPreviewRow row", code);
    }
}
