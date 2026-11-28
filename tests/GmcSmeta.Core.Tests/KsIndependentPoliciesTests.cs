using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsIndependentPoliciesTests
{
    [Theory]
    [InlineData("r", "r", "r", "r", 1, 1, true, true)]
    [InlineData("r", "r", "r", "r", 0, 0, true, true)]
    [InlineData("r", "r", "r", "r", 1, 2, true, false)]
    [InlineData("r", "r", "r", "r", 1, 1, false, false)]
    [InlineData("r", "r", "r", "R", 1, 1, true, false)]
    [InlineData("r", "r", "x", "r", 1, 1, true, false)]
    [InlineData("r", "x", "r", "r", 1, 1, true, false)]
    [InlineData(" ", " ", " ", " ", 0, 0, true, false)]
    public void ReadReuseRequiresExactRevisionAndSelectedAct(string published, string displayed,
        string list, string observed, long publishedId, long selectedId, bool clean, bool expected) =>
        Assert.Equal(expected, KsReadRevisionPolicy.CanReuse(published, displayed, list, observed,
            publishedId, selectedId, clean));

    [Theory]
    [InlineData("r", "r", true)]
    [InlineData("r", "R", false)]
    [InlineData("", "", false)]
    [InlineData(" ", " ", false)]
    public void ReadFenceRequiresNonemptyExactRevision(string before, string after, bool expected) =>
        Assert.Equal(expected, KsReadRevisionPolicy.IsStable(before, after));

    [Fact]
    public void SelectionExpandsSectionsDeduplicatesAndPreservesFirstOccurrence()
    {
        KsBatchPosition P(long id, bool extra = false, bool linked = false) => new(id, 10, 3, 1, 100, extra, linked);
        KsBatchSelectionRow R(long id, long system = 5, bool section = false) => new(section, system, id);
        var selected = KsBatchSelection.Resolve(
            new[] { P(1), P(2, linked: true), P(3, extra: true), P(4) },
            new[] { R(0, section: true), R(1), R(2), R(3), R(4, 6), R(99) },
            new[] { R(2), R(0, section: true), R(3), R(99) });
        Assert.Equal(new long[] { 2, 1, 3 }, selected.Select(p => p.PositionId));
        Assert.True(selected[0].Linked);
        Assert.True(selected[2].OutOfKp);
        Assert.Equal(new[] { (1L, 7m) }, KsBatchQuantities.FillRemaining(selected));
    }

    [Fact]
    public void EmptyAndMissingSelectionsDoNotProduceEdits()
    {
        Assert.Empty(KsBatchSelection.Resolve([], [], [new(false, 1, 9), new(true, 1, 0)]));
    }

    private sealed class Book : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void WorkbookRendererDisposesDocumentAndLeavesStreamOpen()
    {
        var book = new Book();
        using var output = new MemoryStream();
        KsExportRendering.SaveWorkbook(output, () => book, _ => true,
            (_, stream) => stream.WriteByte(42), CancellationToken.None);
        Assert.True(book.Disposed);
        Assert.True(output.CanWrite);
        Assert.Equal(new byte[] { 42 }, output.ToArray());
    }

    [Fact]
    public void EmptyWorkbookIsRejectedAndDisposedBeforeSave()
    {
        var book = new Book();
        using var output = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => KsExportRendering.SaveWorkbook(output, () => book,
            _ => false, (_, stream) => stream.WriteByte(42), CancellationToken.None));
        Assert.True(book.Disposed);
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void CancellationAfterBuildPreventsWorkbookAndPdfWrites()
    {
        var book = new Book();
        using var output = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => KsExportRendering.SaveWorkbook(output,
            () => { cancellation.Cancel(); return book; }, _ => true,
            (_, stream) => stream.WriteByte(42), cancellation.Token));
        Assert.True(book.Disposed);
        using var pdfCancel = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() => KsExportRendering.SavePdf(output,
            () => { pdfCancel.Cancel(); return new byte[] { 42 }; }, pdfCancel.Token));
        Assert.Empty(output.ToArray());
    }

    [Fact]
    public void PdfRendererWritesExactBytesWithoutOwningStream()
    {
        using var output = new MemoryStream();
        KsExportRendering.SavePdf(output, () => new byte[] { 1, 2, 3 }, CancellationToken.None);
        Assert.True(output.CanWrite);
        Assert.Equal(new byte[] { 1, 2, 3 }, output.ToArray());
    }
}
