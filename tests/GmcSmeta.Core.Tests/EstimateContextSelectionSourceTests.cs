namespace GmcSmeta.Core.Tests;

public sealed class EstimateContextSelectionSourceTests
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
    public void EmptyFilteredGridCannotUseHiddenAnchor()
    {
        var menu = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("hit is null && (EstimateGrid.Items.Count > 0 || _rows.Count > 0", menu);
        Assert.Contains("emptyDocument = hit is null && _rows.Count == 0 && selection.Length == 0", menu);
        Assert.Contains("canWrite && (ordinaryAnchor || emptyDocument)", menu);
        Assert.Contains("canWrite && ordinaryAnchor && targetCell.IsValid", menu);
    }

    [Fact]
    public void MixedAndVirtualSelectionsCannotSilentlyRunPartialStructuralCommands()
    {
        var menu = Source("EstimateView.TemporaryView.cs");
        Assert.Contains("!row.IsVirtual && _rows.Contains(row)", menu);
        Assert.Contains("!row.IsSystem && !row.IsAreaSummary && row.Id > 0", menu);
        Assert.Contains("canWrite && positionsOnly", menu);
        Assert.Contains("canWrite && rootsOnly && selection.All", menu);
        Assert.Contains("var bulkPositions = nSel > 1 && positionsOnly;", menu);
        Assert.Contains("if (!item.IsEnabled || !SelectionCurrent()", menu);
        Assert.Contains("SequenceEqual(selection, ReferenceEqualityComparer.Instance)", menu);
        Assert.Contains("ReferenceEquals(document, _estimate)", menu);
    }

    [Fact]
    public void SectionDialogsRetainRevisionAndSelectionUntilWrite()
    {
        var code = Source("EstimateView.Sections.cs");
        Assert.Contains("name, subsection, anchorId, revision));", code);
        Assert.Contains("systemId, name, revision));", code);
        Assert.Contains("SqlServerDatabase.DeleteSection(_sqlConnectionString, _contractId, sectionId, revision)", code);
        Assert.Contains("SqlServerDatabase.DragPositions(_sqlConnectionString, _contractId, picked, to, anchor, revision)", code);
        Assert.Contains("!ReferenceEquals(primary, EstimateGrid.SelectedItem)", code);
        Assert.Contains("!ReferenceEquals(EstimateGrid.SelectedItem, selected)", code);
        Assert.Contains("showDeleteFailure: true", code);
    }

    [Fact]
    public void CopySystemUsesCurrentRealRowAndItsOwningSection()
    {
        var menu = Source("EstimateView.TemporaryView.cs");
        var cells = Source("EstimateView.Cells.cs");
        Assert.Contains("ordinaryAnchor && CopySourceSystem(selected) is not null", menu);
        Assert.Contains("selected is null || selected.IsVirtual || !_rows.Contains(selected)", cells);
        Assert.Contains("SystemCopySource.Resolve(selected.Source", cells);
        Assert.Contains("!ReferenceEquals(CopySourceSystem(selected), source)", cells);
    }

    [Fact]
    public void DirectClipboardAndStructuralCommandsDoNotDropMixedSelections()
    {
        var guard = Source("EstimateView.CommandSelection.cs");
        Assert.Contains("!row.IsVirtual && !row.IsAreaSummary", guard);
        Assert.Contains("rows.All(current.Contains)", guard);
        Assert.Contains("SequenceEqual(rows, ReferenceEqualityComparer.Instance)", guard);
        Assert.Contains("main.IsCurrentEstimatePage(_contractId, _objectId, this)", guard);
        Assert.Contains("revision == _serverRevision && revision == _publishedEstimateRevision", guard);
        var clipboard = Source("EstimateView.Clipboard.cs");
        Assert.Contains("!CommandSelectionCurrent(selected) || selected.Any(row => row.IsSystem", clipboard);
        Assert.Contains("var last = _rows.LastOrDefault(members.Contains)", clipboard);
        Assert.Contains("!CommandWriteCurrent(snapshot, revision) || !CommandSelectionCurrent(selected)", clipboard);
        Assert.Contains("row.Source.ParentPositionId is not null || row.Source.OutOfKp", clipboard);
        var sections = Source("EstimateView.Sections.cs");
        Assert.Contains("targetCurrent: () => CommandWriteCurrent(snapshot, revision)", sections);
        Assert.Contains("!ActiveEstimateWriter()", sections);
        Assert.Contains("selected.IsPendingInsert && anchorDraft is null", sections);
        Assert.Contains("var realAfterId = anchorDraft is null ? afterId : ResolvePendingAnchor(anchorDraft)", sections);
        var import = Source("EstimateView.Import.cs");
        Assert.Contains("!CommandWriteCurrent(snapshot, revision) || !TargetCurrent()", import);
        var numeric = Source("EstimateView.Work.cs");
        Assert.Contains("!EstimateGrid.CurrentCell.Equals(anchor)", numeric);
        Assert.Contains("!CommandWriteCurrent(snapshot, revision) || !TargetsCurrent()", numeric);
        Assert.Contains("_sqlConnectionString, _contractId, field, edits, revision", numeric);
    }

    [Fact]
    public void StructuralCommandsRejectProductionBeforeOpeningSqlConnection()
    {
        const string connection = "Server=.;Database=GmcSmetaProduction;Trusted_Connection=True";
        const string revision = "test-revision";
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.AddPosition(connection, 1, 2, null, null, revision));
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.MovePosition(connection, 1, 2, 3, 1, revision));
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.DeletePositions(connection, 1, new long[] { 3 }, revision));
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.AddSection(connection, 1, "Тест", false, null, revision));
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.RenameSection(connection, 1, 2, "Тест", revision));
        Assert.Throws<InvalidOperationException>(() =>
            GmcSmeta.Core.Db.SqlServerDatabase.DeleteSection(connection, 1, 2, revision));
    }
}
