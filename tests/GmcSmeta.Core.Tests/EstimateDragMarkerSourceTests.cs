namespace GmcSmeta.Core.Tests;

public sealed class EstimateDragMarkerSourceTests
{
    [Fact]
    public void InsertionIndicatorIsLayoutIndependentAndDropRevalidatesTarget()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.DragDrop.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var drag = File.ReadAllText(Path.Combine(app, "EstimateView.DragDrop.cs"));
        var marker = File.ReadAllText(Path.Combine(app, "EstimateInsertionAdorner.cs"));
        var layout = File.ReadAllText(Path.Combine(app, "EstimateView.ColumnLayout.cs"));
        Assert.Contains("EstimateGrid.InputHitTest(point)", drag);
        Assert.Contains("new Rect(EstimateGrid.RenderSize).Contains(point)", drag);
        Assert.Contains("TryDragTarget(e, out var source", drag);
        Assert.Contains("_dragMarkerLayer?.Remove(_dragMarker)", drag);
        Assert.DoesNotContain("row.BorderThickness =", drag);
        Assert.Contains("IsHitTestVisible = false", marker);
        Assert.Contains("ReferenceEquals(_item, row.Item)", marker);
        Assert.Contains("ScrollContentPresenter", marker);
        Assert.Contains("dc.DrawLine", marker);
        Assert.Contains("RefreshDragMarkerAfterScroll()", layout);
    }
}
