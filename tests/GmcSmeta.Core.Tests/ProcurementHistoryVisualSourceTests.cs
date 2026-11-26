using System.Xml.Linq;

namespace GmcSmeta.Core.Tests;

public sealed class ProcurementHistoryVisualSourceTests
{
    [Fact]
    public void CompactHistoryKeepsExpanderEventsAndProtectedActions()
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
        var expander = Named("MovementHistoryExpander");
        Assert.Equal(p + "Expander", expander.Name);
        Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        Assert.Equal("MovementHistory_Expanded", (string?)expander.Attribute("Expanded"));
        Assert.Equal("MovementHistory_Collapsed", (string?)expander.Attribute("Collapsed"));
        Assert.Equal("Collapsed", (string?)Named("HistoryContent").Attribute("Visibility"));
        Assert.Contains(view.Descendants(p + "ToggleButton"), e =>
            ((string?)e.Attribute("IsChecked") ?? "").Contains("IsExpanded"));
        Assert.Equal("MovementEdit_Click", (string?)Named("MovementEditButton").Attribute("Click"));
        Assert.Equal("MovementDelete_Click", (string?)Named("MovementDeleteButton").Attribute("Click"));
        Assert.Equal("False", (string?)Named("MovementDeleteButton").Attribute("IsEnabled"));
        Assert.Contains(Named("MovementDeleteButton").Descendants(p + "Path"), e =>
            (string?)e.Attribute("Data") == "{StaticResource GlyphTrash}");
        Assert.Equal("ProcurementSaveDetails_Click", (string?)Named("SaveDetailButton").Attribute("Click"));
        Assert.Equal("True", (string?)Named("MovementHistoryGrid").Attribute("IsReadOnly"));
        Assert.NotNull(Named("MovementDetailsText"));
        var source = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("Движение и замена: {row.BuyName}", source);
        Assert.Contains("SqlServerProcurement.MovementDeletionError", source);
        Assert.Contains("CancelMovementHistory();", source);
    }
}
