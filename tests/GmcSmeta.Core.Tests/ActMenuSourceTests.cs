using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ActMenuSourceTests
{
    [Fact]
    public void BothMoreMenusKeepHandlersAndSharedIcons()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        XNamespace wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(app, "ActsEditorView.xaml"));
        var code = File.ReadAllText(Path.Combine(app, "ActsEditorView.xaml.cs"));
        var expected = new (string Click, string Name, string Glyph)[]
        {
            ("PreSignCheck_Click", "PreSignMenuItem", "Alert"),
            ("ExportVor_Click", "VorMenuItem", "Download"),
            ("ExportAll_Click", "ExportAllMenuItem", "Download"),
            ("ExportGroup_Click", "ExportGroupMenuItem", "Download"),
            ("AddExtra_Click", "ExtraMenuItem", "Plus"),
            ("DeleteAct_Click", "DeleteActMenuItem", "Trash"),
            ("ActDiscount_Click", "ActDiscountMenuItem", "Edit"),
            ("SectionAmounts_Click", "SectionAmountsMenuItem", "Layers"),
            ("FillIssued_Click", "IssuedMenuItem", "Upload"),
            ("FillAllRest_Click", "RestAllMenuItem", "Check"),
        };
        foreach (var (click, name, glyph) in expected)
        {
            var item = Assert.Single(document.Descendants(wpf + "MenuItem")
                .Where(e => (string?)e.Attribute("Click") == click));
            Assert.Equal(name, (string?)item.Attribute(xaml + "Name"));
            Assert.Null(item.Attribute("Foreground"));
            Assert.Contains("ContextMenuIcons.Set(" + name + ", \"" + glyph + "\"", code);
        }
        Assert.Contains("ContextMenuIcons.Set(DeleteActMenuItem, \"Trash\", danger: true);", code);
        Assert.Contains("DeleteActMenuItem.IsEnabled = !_signed && !_contractLocked;", code);
        Assert.Contains("ExtraMenuItem.IsEnabled = !_signed && !_contractLocked;", code);
        Assert.Contains("IssuedMenuItem.IsEnabled = !_signed && !_contractLocked;", code);
        Assert.Equal(2, document.Descendants(wpf + "ContextMenu")
            .Count(e => (string?)e.Attribute("Opened") == "ActMoreMenu_Opened"));
        Assert.Contains("var ready = CaptureActCommand() is not null && current is not null && _sheet is not null;", code);
        Assert.Contains("DeleteActMenuItem.IsEnabled = editable;", code);
        Assert.Contains("ExtraMenuItem.IsEnabled = IssuedMenuItem.IsEnabled = editable;", code);
        Assert.Contains("RestAllMenuItem.IsEnabled = editable && !manualSections && _totals is not null;", code);
    }
}
