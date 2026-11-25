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
        Assert.Contains("\"back\", \"ArrowBack\"", code);
        Assert.Contains("\"back_supplier\", \"ArrowBack\"", code);
        Assert.Contains("\"in\", \"Download\", \"из цеха\"", code);
        Assert.Contains("enabled: canWrite && singleExtra", code);
        Assert.Contains("enabled: canWrite && extraSelected", code);
        var movements = File.ReadAllText(Path.Combine(app, "ProcurementPane.MovementFlows.cs"));
        Assert.Contains("SqlServerProcurement.IssueViaListBatch", movements);
    }
}
