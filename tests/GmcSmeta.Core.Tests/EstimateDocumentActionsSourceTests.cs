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

    [Fact]
    public void LegacyDocumentCommandsKeepPublishedRevisionAcrossEntryPoints()
    {
        var header = Source("EstimateView.Header.cs");
        foreach (var method in new[] { "OpenContractProperties", "CopyCurrentDocument",
            "MakeCurrentDocumentContract", "CreateDopAgreement", "MoveCurrentContract",
            "DeleteCurrentDocument", "CreateGmcsLink", "CompareCurrentDocument" })
            Assert.Contains($"main.{method}(_doc, _publishedEstimateRevision);", header);
        Assert.Contains("main.OpenEstimateObjectTool(this, _doc, _publishedEstimateRevision, orders: true)", header);
        var main = Source("MainWindow.xaml.cs");
        Assert.Contains("expectedRevision: revision", main);
        Assert.Contains("confirmedRevision != expectedRevision", main);
        var sql = Source("MainWindow.SqlServer.cs");
        Assert.Contains("revision != expectedRevision", sql);
        Assert.Contains("!CanStartDocumentMutation(source)", sql);
        Assert.Contains("DocumentLocallyCurrent(d) && CanStartDocumentMutation(d)", sql);
        Assert.Contains("captured.Revision != expectedRevision", Source("MainWindow.ContractTransfer.cs"));
        Assert.Contains("revision != expectedRevision", Source("MainWindow.DocumentLinks.cs"));
        var tools = Source("MainWindow.DocumentMutation.cs");
        Assert.Contains("captured.Revision != revision", tools);
        Assert.Contains("!DocumentMatchesServer(doc, revision)", tools);
        Assert.Contains("ReferenceEquals(document, _doc)", Source("EstimateView.DocumentActions.cs"));
    }
}
