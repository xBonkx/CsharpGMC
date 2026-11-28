namespace GmcSmeta.Core.Tests;

public sealed class EstimateDocumentActionsSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void NativeMenuCapturesSnapshotAndChecksEveryCommandAgain()
    {
        var menu = Source("EstimateView.DocumentActions.cs");
        Assert.Contains("new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom }", menu);
        Assert.Contains("ReferenceEquals(snapshot, _estimate) && revision == _serverRevision", menu);
        Assert.Contains("main.IsCurrentEstimatePage(_contractId, _objectId, this)", menu);
        Assert.Contains("if (!item.IsEnabled || !allowed())", menu);
        Assert.Contains("menu.IsOpen = false;\n                handler(s, args);", menu);
        Assert.Contains("SequenceEqual(selection, ReferenceEqualityComparer.Instance)", menu);
        Assert.DoesNotContain("ExtraActionsExpander", Source("EstimateView.xaml"));
    }

    [Fact]
    public void ModalChangesNeverUseARevisionReadAfterConfirmation()
    {
        var area = Source("EstimateView.AreaCollapse.cs");
        Assert.Contains("ExecuteAreaTransformAsync(whole, null, false, revision)", area);
        Assert.Contains("revision != _serverRevision", area);
        var cells = Source("EstimateView.Cells.cs");
        Assert.Contains("_sqlConnectionString, _contractId, ids, value, revision", cells);
        Assert.Contains("revision != displayedRevision", cells);
        Assert.Contains("CanChangeHeader(estimate, baselineRevision, false)", cells);
        var spec = Source("EstimateView.SpecUpdate.cs");
        Assert.Contains("dialog.ShowDialog() != true || !CanChangeHeader(snapshot, revision, false)", spec);
        Assert.Contains("incoming, keys, keepOrder, revision", spec);
    }
}
