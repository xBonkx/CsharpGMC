namespace GmcSmeta.Core.Tests;

public sealed class ProcurementToolbarMenuSourceTests
{
    [Fact]
    public void ToolbarUsesSnapshotAndExistingMovementWorkflow()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ProcurementPane.Navigation.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var code = File.ReadAllText(Path.Combine(app, "ProcurementPane.Navigation.cs"));
        Assert.Contains("if (!CanStartOperation || SelectedBuyingIds().Count == 0) return;", code);
        Assert.Contains("_attachmentGeneration == attachment && _revision == revision", code);
        Assert.Contains("SelectedBuyingIds().OrderBy(id => id).SequenceEqual(ids)", code);
        Assert.Contains("OpenMovementDialogAsync(row, kind, defaultDocument: document)", code);
        Assert.Contains("Movement(\"Возврат с объекта…\", \"back\");", code);
        Assert.Contains("Movement(\"Возврат поставщику…\", \"back_supplier\");", code);
        Assert.Contains("Movement(\"Изготовлено → на склад…\", \"in\", document: \"из цеха\");", code);
        Assert.Contains("Guard(ProcurementToShop_Click), \"Settings\"", code);
        var grid = File.ReadAllText(Path.Combine(app, "ProcurementPane.xaml.cs"));
        Assert.Contains("ProcurementBulkSupplier_Click, \"Building\");", grid);
        Assert.Contains("ProcurementToShop_Click, \"Settings\");", grid);
        Assert.Contains("Add(\"Возврат с объекта…\", \"back\");", grid);
        Assert.Contains("Add(\"Возврат поставщику…\", \"back_supplier\");", grid);
        Assert.Contains("Add(\"Изготовлено → на склад…\", \"in\", defaultDocument: \"из цеха\");", grid);
        Assert.Contains("enabled: canWrite && singleExtra", code);
        Assert.Contains("enabled: canWrite && extraSelected", code);
        var movements = File.ReadAllText(Path.Combine(app, "ProcurementPane.MovementFlows.cs"));
        Assert.Contains("SqlServerProcurement.IssueViaListBatch", movements);
    }
}
