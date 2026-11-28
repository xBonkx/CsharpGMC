namespace GmcSmeta.Core.Tests;

public sealed class ColumnFitLayoutLifecycleSourceTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void HeaderReuseKeepsOriginWidthsFontAndDpiInItsIdentity()
    {
        var code = Source("SummaryHeaderBands.cs");
        Assert.Contains("_lastOrigin == origin", code);
        Assert.Contains("_lastColumns.AsSpan().SequenceEqual(columns)", code);
        Assert.Contains("SetGeometry(viewport, _runs);", code);
        Assert.Contains("_dpi == dpi && Equals(_renderFamily, family)", code);
        Assert.Contains("_lastColumns = [];", code);
        Assert.Contains("pen.Freeze();", code);
        Assert.Contains("GlyphPens[run.Group]", code);
        Assert.DoesNotContain("new Pen(design.Icon", code);
    }

    [Theory]
    [InlineData("SummaryColumnFit.cs", "InvalidateMeasurements()")]
    [InlineData("LegacyCostColumnFit.cs", "Invalidate()")]
    public void ChangedCollectionOrCommittedEditInvalidatesWidths(string file, string invalidate)
    {
        var code = Source(file);
        Assert.Contains("CollectionChangedEventManager.AddHandler(grid.Items, (_, _) => " + invalidate + ")", code);
        Assert.Contains("if (e.EditAction == DataGridEditAction.Commit) " + invalidate, code);
        Assert.Contains("WeakReference<object>", code);
        Assert.Contains("DispatcherPriority.Loaded", code);
    }
}
