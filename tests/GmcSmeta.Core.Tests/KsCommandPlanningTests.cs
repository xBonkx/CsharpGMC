using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsCommandPlanningTests
{
    private static SqlServerActs.ActSheetRow P(long id, long system, decimal line = 0m,
        bool extra = false, string formula = "", decimal closed = 3m) =>
        new(id, system, "", "A", "шт", 10m, 100m, 0m, extra, "", null, line, formula,
            100m, 0m, closed, 0m, false);

    [Fact]
    public void FillAllRestSkipsLinkedExtraAndUnchangedRows()
    {
        var plan = KsClosePlan.AllRemaining(new[] { P(1, 1), P(2, 1, line: 7m),
            P(3, 1, extra: true), P(4, 1, formula: "@1"), P(5, 1, line: 2m, closed: 11m) });
        Assert.Equal(new[] { (1L, 7m), (5L, 0m) }, plan.Edits);
        Assert.Equal(700m, plan.Amount);
        Assert.True(plan.HasLinked);
    }

    [Fact]
    public void SectionPreviewIncludesDirectChildrenAndKeepsExistingLinkedRule()
    {
        var systems = new SqlServerActs.ActSheetSystem[]
        { new(1, "A", false, 0), new(2, "B", true, 1), new(3, "C", false, 0), new(4, "D", true, 2) };
        var rows = new[] { P(2, 2, formula: "@1"), P(1, 1), P(3, 2, line: 7m),
            P(4, 1, extra: true), P(5, 3), P(6, 4) };
        var plan = KsClosePlan.Sections(systems, rows);
        Assert.Equal(new[] { (1L, "A", 2100m), (3L, "C", 700m) }, plan.Options);
        Assert.Equal(new[] { (2L, 7m), (1L, 7m) }, plan.EditsFor(new long[] { 1, 1, 99 }));
        Assert.Empty(plan.EditsFor(new long[] { 99 }));
    }

    [Theory]
    [InlineData(false, 50)]
    [InlineData(true, null)]
    public void ChargesClampOnlyManualRemainders(bool positionSmr, int? expectedSmr)
    {
        var charges = KsClosePlan.RemainingCharges(100m, 25m, 9m,
            new[] { (20m, 30m, 2m), (30m, 1m, 3m) }, positionSmr);
        Assert.Equal((decimal?)expectedSmr, charges.Smr);
        Assert.Equal(0m, charges.Transport);
        Assert.Equal(4m, charges.Pnr);
    }

    [Theory]
    [InlineData("a", 1, "r", "a", 1, "r", true)]
    [InlineData("a", 1, "r", "b", 1, "r", false)]
    [InlineData("a", 1, "r", "a", 2, "r", false)]
    [InlineData("a", 1, "r", "a", 1, "s", false)]
    [InlineData("a", 1, "", "a", 1, "", false)]
    public void CachedContextRequiresExactDatabaseDocumentRevision(string cachedConnection, int cachedContract,
        string cachedRevision, string connection, int contract, string revision, bool expected) =>
        Assert.Equal(expected, KsReadRevisionPolicy.CanUseCachedContext(cachedConnection, cachedContract,
            cachedRevision, connection, contract, revision));

    [Theory]
    [InlineData(1, 2, 1, 2, true)]
    [InlineData(1, 2, 2, 2, false)]
    [InlineData(1, 2, 1, 3, false)]
    public void DialogTargetCannotSurviveReloadOrReattach(long capturedRead, long capturedAttachment,
        long read, long attachment, bool expected) =>
        Assert.Equal(expected, KsReadRevisionPolicy.IsSameGeneration(capturedRead, capturedAttachment, read, attachment));

    [Fact]
    public void SectionDialogValidatesOriginalSheetAndTargetBeforeWrite()
    {
        var code = Source("ActsEditorView.xaml.cs");
        var start = code.IndexOf("private async void CloseSections_Click", StringComparison.Ordinal);
        var end = code.IndexOf("private async Task SaveBatchAsync", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var section = code[start..end];
        var dialog = section.IndexOf("KsCloseSectionsDialog.Show", StringComparison.Ordinal);
        var guard = section.IndexOf("!IsCurrentActCommand(target) || !ReferenceEquals(sheet, _sheet)", StringComparison.Ordinal);
        var write = section.IndexOf("await SaveBatchAsync(edits,", StringComparison.Ordinal);
        Assert.True(dialog >= 0 && guard > dialog && write > guard);
        Assert.Contains("KsReadRevisionPolicy.IsSameGeneration(target.ReadGeneration, target.AttachmentGeneration", code);
    }

    [Fact]
    public void ExportRequiresPreDialogGenerationBeforeStartingWorker()
    {
        var code = Source("ActsEditorView.Export.cs");
        var start = code.IndexOf("private async Task ExportActFileAsync", StringComparison.Ordinal);
        var export = code[start..];
        var guard = export.IndexOf("!IsVisible || !IsCurrentActCommand(target)", StringComparison.Ordinal);
        var startWorker = export.IndexOf("_exportInProgress = true;", StringComparison.Ordinal);
        Assert.True(guard >= 0 && startWorker > guard);
        Assert.Contains("var generation = target.ReadGeneration;", export);
        Assert.Contains("var attachment = target.AttachmentGeneration;", export);
    }

    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }
}
