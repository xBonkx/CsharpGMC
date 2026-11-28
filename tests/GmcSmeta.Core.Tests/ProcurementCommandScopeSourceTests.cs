namespace GmcSmeta.Core.Tests;

public sealed class ProcurementCommandScopeSourceTests
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
    public void LeaseRetainsOriginalRevisionAndSelectionAndIsSingleUse()
    {
        var ui = Source("GmcSmeta.App/ProcurementPane.xaml.cs");
        Assert.Contains("_generation == _owner._reloadGeneration", ui);
        Assert.Contains("ReferenceEquals(_rows, _owner._allRows) && _owner._revision == Revision", ui);
        Assert.Contains("ReferenceEquals(_primary, _owner.BuyGrid.SelectedItem)", ui);
        Assert.Contains("!IsCurrent() || !_owner._snapshotCanWrite", ui);
        Assert.Contains("_writeStarted = true;", ui);
        Assert.Contains("MessageBoxImage.Warning, MessageBoxResult.No", ui);
        Assert.Contains("CurrentHistoryTarget(row, movement, operation.Revision)", ui);
        var load = Source("GmcSmeta.App/ProcurementPane.Loading.cs");
        Assert.Contains("_snapshotCanWrite = accepted.CanWrite;", load);
        Assert.Contains("BuyGrid.IsReadOnly = !_snapshotCanWrite;", load);
        var sql = Source("GmcSmeta.Core/Db/SqlServerProcurement.cs");
        Assert.Contains("SystemChoicesQuery + \";\\n\" + writeScope", sql);
        Assert.Contains("CanWrite = editable && SqlSchemaCompatibility.IsWritablePilot", sql);
    }

    [Fact]
    public void MovementAndExtraCommandsRejectMixedOrChangedSelection()
    {
        var selection = Source("GmcSmeta.App/ProcurementPane.CommandScope.cs");
        Assert.Contains("selected.Count != BuyGrid.SelectedItems.Count", selection);
        Assert.Contains("selected.All(row => all.Contains(row) && visible.Contains(row))", selection);
        var movement = Source("GmcSmeta.App/ProcurementPane.MovementFlows.cs");
        Assert.Contains("items.Any(item => !allowed.Contains(item.PositionId))", movement);
        Assert.Contains("CurrentHistoryTarget(position, movement, operation.Revision)", movement);
        Assert.Contains("date, document, note, revision", movement);
        var extras = Source("GmcSmeta.App/ProcurementPane.BulkExtras.cs");
        Assert.Contains("selected.Any(row => !row.OutOfKp || row.IsSub)", extras);
        Assert.Contains("!preview.Decisions.Select(x => x.PositionId).SequenceEqual(ids)", extras);
        Assert.Contains("MessageBoxImage.Warning, MessageBoxResult.No", extras);
        var menu = Source("GmcSmeta.App/ProcurementPane.Navigation.cs");
        Assert.Contains("Guard(ProcurementDeleteExtra_Click)", menu);
        Assert.Contains("selected.All(row => row.OutOfKp && !row.IsSub)", menu);
    }

    [Fact]
    public void SqlChecksActiveScopeAndExactSupplementRoots()
    {
        foreach (var file in new[] { "SqlServerProcurement.cs", "SqlServerProcurement.Movements.cs",
            "SqlServerProcurement.Extras.cs", "SqlServerProcurement.BulkExtras.cs", "SqlServerProcurement.SheetImport.cs" })
        {
            var sql = Source("GmcSmeta.Core/Db/" + file);
            Assert.Contains("ISNULL(o.archived,0)=0", sql);
            Assert.Contains("ISNULL(c.stage_rest,0)=0", sql);
        }
        var extra = Source("GmcSmeta.Core/Db/SqlServerProcurement.Extras.cs");
        Assert.Contains(".SequenceEqual(expectedRoots.OrderBy(id => id))", extra);
        Assert.Contains("IsolationLevel.Serializable", extra);
    }
}
