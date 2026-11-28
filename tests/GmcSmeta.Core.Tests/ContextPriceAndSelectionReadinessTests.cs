using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ContextPriceAndSelectionReadinessTests
{
    [Theory]
    [InlineData(false, false, false, false, "rev", true)]
    [InlineData(true, false, false, false, "rev", false)]
    [InlineData(null, false, false, false, "rev", false)]
    [InlineData(false, true, false, false, "rev", false)]
    [InlineData(false, false, true, false, "rev", false)]
    [InlineData(false, false, false, true, "rev", false)]
    [InlineData(false, false, false, false, "", false)]
    [InlineData(false, false, false, false, " ", false)]
    public void PriceLikeRequiresWritableSettledSnapshot(bool? locked, bool conflict,
        bool unsettled, bool detached, string revision, bool expected) =>
        Assert.Equal(expected, PriceLikeBatchPlan.CanStart(locked, conflict, unsettled, detached, revision));

    private static string Source(string directory, string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", directory, file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", directory, file));
    }

    [Fact]
    public void PriceLikeWriterKeepsOriginalRevisionAfterQueueWait()
    {
        var code = Source("GmcSmeta.App", "EstimateView.Cells.cs");
        var start = code.IndexOf("private async Task SavePriceLikeAsync", StringComparison.Ordinal);
        var end = code.IndexOf("private static TextBox? FindTextBox", start, StringComparison.Ordinal);
        var command = code[start..end];
        Assert.Contains("if (!CanStartPriceLike()", command);
        Assert.Contains("var revision = _serverRevision;", command);
        Assert.Contains("AttachSaveCloseGuard();", command);
        Assert.Contains("revision != _serverRevision || !ReferenceEquals(snapshot, _estimate)", command);
        Assert.Contains("sourceId, field, revision, expectedPlan));", command);
        Assert.Contains("!selectionCurrent() || HasActiveEstimateEdit()", command);
        Assert.Contains("_saveAwaitingReadback = true;", command);
        var sql = Source("GmcSmeta.Core", "Db/SqlServerPriceLike.cs");
        Assert.Contains("if (locked)", sql);
        Assert.Contains("SqlSchemaCompatibility.IsWritablePilot", sql);
        Assert.Contains("IsolationLevel.Serializable", sql);
        Assert.Contains("string.Equals(revision, expectedRevision", sql);
        Assert.Contains("!PriceLikeBatchPlan.PositivePrice(plan.Price)", sql);
        Assert.Contains("!PriceLikeBatchPlan.Matches(expectedPlan, plan)", sql);
        Assert.Contains("o.archived", sql);
    }

    [Fact]
    public void ProcurementMenuRequiresCurrentRealRowsAndAttachment()
    {
        var code = Source("GmcSmeta.App", "ProcurementPane.xaml.cs");
        Assert.Contains("ContextMenuTarget.SelectRow<ProcurementPreviewRow>", code);
        Assert.Contains("row.Work || row.PositionId <= 0", code);
        Assert.Contains("attachment == _attachmentGeneration", code);
        Assert.Contains("SequenceEqual(selection, ReferenceEqualityComparer.Instance)", code);
        Assert.Contains("ids.Contains(row.PositionId)", code);
        Assert.Contains("_historyRevision != _revision", code);
        var movement = Source("GmcSmeta.App", "ProcurementPane.MovementFlows.cs");
        Assert.Contains("if (!valid.Contains(clicked))", movement);
        Assert.Contains(".Where(valid.Contains)", movement);
        Assert.Contains("operation.BeginWrite()", movement);
    }

    [Fact]
    public void MaterialsMenuPreservesActualSelectionAndExplicitGroupExpansion()
    {
        var code = Source("GmcSmeta.App", "EstimateView.Materials.cs");
        Assert.Contains("ContextMenuTarget.SelectRow<MaterialsRow>", code);
        Assert.Contains("MaterialsGrid.Items.Contains(hit)", code);
        Assert.Contains("SequenceEqual(selection, ReferenceEqualityComparer.Instance)", code);
        Assert.Contains("if (row.IsSystemHeader && !row.IsGroup)", code);
        Assert.Contains("foreach (var id in row.MemberIds) Add(id);", code);
        Assert.Contains("row.PositionId > 0 && !row.IsSub", code);
    }
}
