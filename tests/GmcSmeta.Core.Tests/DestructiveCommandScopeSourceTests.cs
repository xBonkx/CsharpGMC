namespace GmcSmeta.Core.Tests;

public sealed class DestructiveCommandScopeSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", file));
    }

    [Fact]
    public void ShipmentWritesCheckExactSnapshotBeforeAndAfterConfirmation()
    {
        var ui = Source("GmcSmeta.App/ShipmentsPane.xaml.cs");
        Assert.Contains("attachment == _attachmentGeneration && load == _loadGeneration", ui);
        Assert.Contains("_shipmentId == captured.Id && Equals(Current, captured)", ui);
        Assert.Contains("_editingFixedId == editingFixed && _revision == revision", ui);
        Assert.Equal(2, ui.Split("if (!CurrentTarget()) return new OperationOutcome").Length - 1);
        Assert.Contains("selected.Any(row => !row.InShipment || !row.CanChange)", ui);
        Assert.Contains("LinesGrid.SelectedItems.Count == 0 && CurrentShipmentFilterEntries(onlyStock, entries)", ui);
        Assert.Contains("targetCurrent: () => CurrentShipmentSelection(selected)", ui);
        Assert.Contains("MessageBoxImage.Question, MessageBoxResult.No", ui);
        Assert.Contains("_connectionString, current.Id, revision, _objectId", ui);
        var sql = Source("GmcSmeta.Core/Db/SqlServerShipments.cs");
        Assert.Equal(7, sql.Split("if (!IsActiveShipment(conn, tx, shipmentId,").Length - 1);
        Assert.Contains("ISNULL(o.archived,0)=0 AND (@object IS NULL OR sh.object_id=@object)", sql);
        Assert.Contains("if (delete.ExecuteNonQuery() != 1)", sql);
    }

    [Fact]
    public void ActDeletionAndSigningRetainContractAndSheetIdentity()
    {
        var ui = Source("GmcSmeta.App/ActsEditorView.xaml.cs");
        Assert.Contains("target.ConnectionString, target.ActId, target.Revision, target.ContractId", ui);
        Assert.Contains("target.ConnectionString, actId, signed, expectedRevision, target.ContractId", ui);
        Assert.Contains("!ReferenceEquals(sheet, _sheet) || !Equals(Current, current)", ui);
        Assert.Contains("if (!CurrentSigningTarget()) return;", ui);
        Assert.Contains("DeleteActMenuItem.IsEnabled = editable;", ui);
        var sql = Source("GmcSmeta.Core/Db/SqlServerActs.cs");
        Assert.Equal(2, sql.Split("if (expectedContractId is long expected && expected != contractId)").Length - 1);
        Assert.Contains("WHERE a.id=@id AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", sql);
    }

    [Fact]
    public void StageQuantityAndDeleteCannotAdoptRevisionAfterConfirmation()
    {
        var ui = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        var start = ui.IndexOf("private async void StagesGrid_CellEditEnding", StringComparison.Ordinal);
        var confirm = ui.IndexOf("Больше остатка", start, StringComparison.Ordinal);
        var captured = ui.IndexOf("var revision = ServerRevision();", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && captured > start && confirm > captured);
        Assert.Contains("!ReferenceEquals(table, _table)", ui);
        Assert.Contains("!StageCommandCurrent(stage, revision)", ui);
        Assert.Contains("SqlServerStages.DeleteStage(_connectionString, stageId, revision, _fullId)", ui);
        var inline = Source("GmcSmeta.App/EstimateView.InlineStages.Commands.cs");
        Assert.Contains("SqlServerStages.DeleteStage(_sqlConnectionString, id, revision, _contractId)", inline);
        var sql = Source("GmcSmeta.Core/Db/SqlServerStages.cs");
        Assert.Equal(2, sql.Split("if (expectedFullId is long expected && expected != fullId)").Length - 1);
        Assert.Contains("SqlServerTrash.KeepContract(conn, tx, stageId, allowStage: true)", sql);
    }
}
