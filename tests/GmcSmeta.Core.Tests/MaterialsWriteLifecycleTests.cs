using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MaterialsWriteLifecycleTests
{
    [Theory]
    [InlineData("r", "r", "r", false, 0, true)]
    [InlineData("r", "r", "s", false, 0, false)]
    [InlineData("r", "s", "r", false, 0, false)]
    [InlineData("", "", "", false, 0, false)]
    [InlineData("r", "r", "r", true, 0, false)]
    [InlineData("r", "r", "r", false, 1, false)]
    public void ModalCommandRequiresCurrentParentAndMaterialsRevision(
        string captured, string material, string parent, bool blocked, int pending, bool expected) =>
        Assert.Equal(expected, MaterialsEditPolicy.CanContinueWrite(captured, material, parent, blocked, pending));

    [Theory]
    [InlineData("a", "a", "b", false, 0, true)]
    [InlineData("a", "b", "b", false, 0, true)]
    [InlineData("a", "c", "b", false, 0, false)]
    [InlineData("a", "a", "", false, 0, false)]
    [InlineData("", "", "b", false, 0, false)]
    [InlineData("a", "a", "b", true, 0, false)]
    [InlineData("a", "a", "b", false, 1, false)]
    public void WriteReceiptCannotRegressOrBypassPendingParent(
        string expected, string current, string receipt, bool conflict, int pending, bool allowed) =>
        Assert.Equal(allowed, MaterialsEditPolicy.CanAcceptReceipt(expected, current, receipt, conflict, pending));

    [Theory]
    [InlineData("Цена клиенту", false, false, true)]
    [InlineData("Цена клиенту", false, true, false)]
    [InlineData("Наименование", true, false, false)]
    [InlineData("Наценка, %", true, false, false)]
    [InlineData("Факт по счёту", false, true, false)]
    [InlineData("Закуп за ед.", true, true, true)]
    [InlineData("Пометки", false, true, true)]
    public void CellCommitRepeatsLockAndComponentRules(string field, bool sub, bool locked, bool allowed) =>
        Assert.Equal(allowed, MaterialsEditPolicy.CellAllowed(field, sub, locked));

    [Fact]
    public void ClearConsumesReceiptBeforeForcedReadbackAndNeverReplaysMutation()
    {
        var source = Source();
        var start = source.IndexOf("private async Task ClearMaterialsAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private void FillMaterialsColorItems", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var clear = source[start..end];
        var write = clear.IndexOf("SqlServerCostOperations.ClearMaterialCells(", StringComparison.Ordinal);
        var receipt = clear.IndexOf("RecordMaterialsWriteReceipt(revision, result.Revision);", StringComparison.Ordinal);
        var readback = clear.IndexOf("await LoadMaterialsAsync(force: true);", StringComparison.Ordinal);
        Assert.True(write >= 0 && receipt > write && readback > receipt);
        Assert.Contains("!CanContinueMaterialsWrite(revision)", clear);
        Assert.Equal(1, clear.Split("SqlServerCostOperations.ClearMaterialCells(", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ProcurementRequestDoesNotMarkStaleOrLockedRowsAfterFileExport()
    {
        var source = Source();
        var start = source.IndexOf("private async void MaterialsRequest_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private void MaterialsExport_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var request = source[start..end];
        Assert.Contains("revision != expectedRevision", request);
        var dialog = request.IndexOf("dialog.ShowDialog()", StringComparison.Ordinal);
        var guard = request.IndexOf("!CanContinueMaterialsWrite(snapshot.revision) || MaterialsLocked", StringComparison.Ordinal);
        var write = request.IndexOf("SqlServerProcurement.SetProc(", StringComparison.Ordinal);
        Assert.True(dialog >= 0 && guard > dialog && write > guard);
        Assert.Contains("RecordMaterialsWriteReceipt(snapshot.revision, saved.Revision)", request);
        Assert.Contains("Excel сохранён, но строки НЕ отмечены заказанными", request);
    }

    private static string Source()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.Materials.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "EstimateView.Materials.cs"));
    }
}
