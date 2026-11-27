using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleWorkspaceSourceTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void DirectoryMenuUsesRetainedTabRatherThanModalWindow()
    {
        var navigation = Source("MainWindow.Navigation.cs");
        Assert.Contains("OpenPeopleWorkspace, \"Users\"", navigation);
        Assert.DoesNotContain("new PeopleWindow(_sqlConnectionString!)", navigation);
        var markup = XDocument.Parse(Source("MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tab = markup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "AuxWorkspaceTabs");
        Assert.Equal("TabControl", tab.Name.LocalName);
        var surface = markup.Descendants().Single(e => (string?)e.Attribute(x + "Name") == "DocumentsWorkspaceSurface");
        Assert.Contains(surface.Descendants(), e => (string?)e.Attribute(x + "Name") == "PageHost");
        Assert.Equal("UserControl", XDocument.Parse(Source("PeopleWindow.xaml")).Root!.Name.LocalName);
    }

    [Fact]
    public void TabSwitchAndCloseRespectCellCommitAndCancelReads()
    {
        var host = Source("MainWindow.PeopleWorkspace.cs");
        Assert.Contains("if (_peopleWorkspace is null)", host);
        Assert.Contains("!view.TryLeave()", host);
        Assert.Contains("view.CloseView();", host);
        Assert.Contains("PeopleWorkspaceHost.Content = null;", host);
        Assert.Contains("_peopleWorkspace?.CloseView();", Source("MainWindow.xaml.cs"));
        Assert.Contains("_peopleWorkspace.CanRestart", Source("MainWindow.UpdateRestart.cs"));
        Assert.Contains("if (!_view.TryLeave()) e.Cancel = true;", Source("PeopleWindow.Host.cs"));
    }
}
