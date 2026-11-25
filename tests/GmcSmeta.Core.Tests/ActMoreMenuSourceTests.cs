using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ActMoreMenuSourceTests
{
    [Fact]
    public void BothActMenusRefreshAvailabilityAndUseSharedGlyphs()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "App.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var menus = XDocument.Load(Path.Combine(app, "ActsEditorView.xaml"))
            .Descendants(presentation + "ContextMenu")
            .Where(e => (string?)e.Attribute("Opened") == "ActMoreMenu_Opened").ToArray();
        Assert.Equal(2, menus.Length);
        var items = menus.SelectMany(e => e.Elements(presentation + "MenuItem")).ToArray();
        Assert.Equal(10, items.Length);
        var code = File.ReadAllText(Path.Combine(app, "ActsEditorView.xaml.cs"));
        foreach (var item in items)
        {
            var name = (string?)item.Attribute(xaml + "Name");
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Contains($"ContextMenuIcons.Set({name},", code);
        }
        Assert.Contains("ContextMenuIcons.Set(DeleteActMenuItem, \"Trash\", danger: true);", code);
        Assert.Contains("var ready = CaptureActCommand() is not null && current is not null && _sheet is not null;", code);
        Assert.Contains("var unsigned = ready && current is { Signed: false };", code);
        Assert.Contains("ExtraMenuItem.IsEnabled = DeleteActMenuItem.IsEnabled = unsigned;", code);
        Assert.Contains("RestAllMenuItem.IsEnabled = editableQuantities && !manualSections && _totals is not null;", code);
    }
}
