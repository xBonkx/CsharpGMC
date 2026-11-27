using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementFilterVisualSourceTests
{
    [Fact]
    public void SearchClearAndSystemPickerPreserveExistingFilters()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var view = XDocument.Load(Path.Combine(app, "ProcurementPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(view.Descendants()
            .Where(e => (string?)e.Attribute(x + "Name") == name));
        var search = Named("ProcurementSearchBox");
        Assert.Equal("ProcurementFilter_Changed", (string?)search.Attribute("TextChanged"));
        var clear = Assert.Single(view.Descendants(p + "Button")
            .Where(e => (string?)e.Attribute("Click") == "ClearProcurementSearch_Click"));
        Assert.Equal("{StaticResource IconOnlyX}", (string?)clear.Attribute("ContentTemplate"));
        var trigger = Assert.Single(clear.Descendants(p + "DataTrigger"));
        Assert.Contains("ElementName=ProcurementSearchBox", (string?)trigger.Attribute("Binding") ?? "");
        Assert.Equal("", (string?)trigger.Attribute("Value"));
        var picker = Named("ProcurementSystemToggle");
        Assert.Equal("150", (string?)picker.Attribute("MinWidth"));
        Assert.Equal("{StaticResource ProcurementSystemPicker}", (string?)picker.Attribute("Style"));
        var style = Assert.Single(view.Descendants(p + "Style")
            .Where(e => (string?)e.Attribute(x + "Key") == "ProcurementSystemPicker"));
        Assert.Contains(style.Descendants(p + "Trigger"), e => (string?)e.Attribute("Property") == "Tag"
            && (string?)e.Attribute("Value") == "filtered");
        Assert.Contains(style.Descendants(p + "Path"), e => (string?)e.Attribute("Data") == "{StaticResource GlyphChevronDown}");
        var nav = File.ReadAllText(Path.Combine(app, "ProcurementPane.Navigation.cs"));
        Assert.Contains("ProcurementSearchBox.Clear();", nav);
        Assert.Contains("ProcurementSearchBox.Focus();", nav);
        var code = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("ProcurementSystemToggle.Tag = selected.Count > 0 ? \"filtered\" : null;", code);
    }

    [Fact]
    public void CategoryClickSurvivesEditAndIsScopedToItsPane()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.xaml")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var doc = XDocument.Load(Path.Combine(app, "ProcurementPane.xaml"));
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var radios = doc.Descendants(p + "RadioButton")
            .Where(e => (string?)e.Attribute("Click") == "ProcurementCategory_Click").ToArray();
        Assert.Equal(8, radios.Length);
        Assert.All(radios, e =>
        {
            Assert.Null(e.Attribute("GroupName"));
            Assert.Null(e.Attribute("Checked"));
            Assert.Same(radios[0].Parent, e.Parent);
        });
        var code = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("_categoryRefreshPending = true;", code);
        Assert.Contains("QueueCategoryRefresh(finishEdit: true);", code);
        Assert.Contains("if (_categoryRefreshPending) QueueCategoryRefresh(finishEdit: false);", code);
        Assert.Contains("_owner.QueueCategoryRefresh(finishEdit: false);", code);
        Assert.Contains("if (_batchBusy) return;", code);
        Assert.Contains("_categoryRefreshPending = false;", code);
    }

    [Fact]
    public void EquivalentScopeChoicesDoNotRebindFilterControls()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ProcurementPane.xaml.cs"));
        Assert.Contains("!existing.SequenceEqual(choices)", code);
        Assert.Contains("existing.Select(choice => (choice.Id, choice.Label, choice.IsSelected)).SequenceEqual(", code);
        Assert.Contains("visible.Select(choice => (choice.Id, choice.Label, _selectedSystemIds.Contains(choice.Id)))", code);
    }
}
