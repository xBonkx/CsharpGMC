namespace GmcSmeta.Core.Tests;

public sealed class ShipmentMenuSourceTests
{
    [Fact]
    public void PostedShipmentMenuExplainsRestrictionAndRetainsDeleteGuards()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ShipmentsPane.xaml.cs"));
        Assert.Contains("DeleteShipmentButton.IsEnabled = true;", code);
        Assert.DoesNotContain("DeleteShipmentButton.IsEnabled = !current.Fixed;", code);
        Assert.Contains("var canDelete = !current.Fixed && !HeaderDirty();", code);
        Assert.Contains("ToolTipService.SetShowOnDisabled(delete, true);", code);
        Assert.Contains("selected.Id == id && ServerRevision() == revision", code);
        Assert.Contains("if (!CanStartWrite || !RequireSavedHeader() || Current is not { Fixed: false } current) return;", code);
        Assert.Contains("confirm: () => Ask(\"Удаление ведомости\"", code);
    }
}
