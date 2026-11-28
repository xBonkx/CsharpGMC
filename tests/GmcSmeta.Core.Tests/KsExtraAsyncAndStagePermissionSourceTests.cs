namespace GmcSmeta.Core.Tests;

public sealed class KsExtraAsyncAndStagePermissionSourceTests
{
    private static string Read(string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", path)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", path));
    }

    [Fact]
    public void ExtraEditDoesNotBlockDispatcherAndReloadsReturnedPositionId()
    {
        var code = Read("GmcSmeta.App/ActsEditorView.xaml.cs");
        var start = code.IndexOf("private async void EditExtraDialog(", StringComparison.Ordinal);
        var end = code.IndexOf("private void Props_Click(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = code[start..end];
        Assert.DoesNotContain("GetAwaiter().GetResult()", method);
        Assert.Contains("await RunActWriteAsync(() => SqlServerActs.UpdateExtraLine(", method);
        Assert.Contains("await ReloadAsync(target.ActId, reuseList: false);", method);
        Assert.Contains("row.PositionId == savedId && row.InAct", method);
        Assert.Contains("Результат сохранения не подтверждён", method);
        Assert.Contains("!ActGrid.CommitEdit(DataGridEditingUnit.Row, true)", method);
    }

    [Fact]
    public void FullStagePermissionUsesSamePolicyForReadsAndTransactionalWrites()
    {
        var sql = Read("GmcSmeta.Core/Db/SqlServerStages.Safety.cs");
        Assert.Contains("FullMutationProblem(conn, null, fullId, token)", sql);
        Assert.Contains("FullMutationProblem(conn, tx, fullId, CancellationToken.None)", sql);
        Assert.Contains("WITH (UPDLOCK,HOLDLOCK)", sql);
        var code = Read("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("ReadFullStageMutationProblem(conn, _fullId, token)", code);
        Assert.Contains("enabled && FullEditable", code);
        Assert.Contains("readOnly: !FullEditable || stage.Locked", code);
        Assert.Contains("FullEditable && !_closed && !_building", code);
    }

    [Fact]
    public void ComputedRestIsNeverEditableEvenIfLegacyLockIsMissing()
    {
        var menu = Read("GmcSmeta.App/EstimateView.InlineStages.cs");
        Assert.Contains("!stage.Rest && !stage.Locked && stage.StageSum is null", menu);
        var commands = Read("GmcSmeta.App/EstimateView.InlineStages.Commands.cs");
        Assert.Contains("stage is null || stage.Rest || stage.Locked", commands);
        var policy = Read("GmcSmeta.Core/Domain/StageMutationPolicy.cs");
        Assert.Contains("if (stage.IsRest)", policy);
        Assert.Contains("ReadFullStageMutationProblem(conn, full, token)", menu);
        Assert.Contains("data.Stages[stageIndex].StageSum is null", menu);
        Assert.Contains("var stage = index == data.Stages.Count ? data.Rest", menu);
        var batch = Read("GmcSmeta.App/EstimateView.InlineStages.Batch.cs");
        Assert.Contains("snapshot.FullMutationProblem is null", batch);
        Assert.Contains("stage.Locked || stage.Rest || stage.StageSum is not null", batch);
        var dialog = Read("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("OpenStageRequested?.Invoke(checked((int)stageId))", dialog);
        Assert.Contains("_restDocument?.Id != stageId", dialog);
    }

    [Fact]
    public void ExtraWritesRejectArchivedObjectsAndComputedRestInsideTransaction()
    {
        var extra = Read("GmcSmeta.Core/Db/SqlServerActs.Extras.cs");
        Assert.Contains("JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id", extra);
        Assert.Contains("AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", extra);
        Assert.Equal(3, extra.Split("var row=LoadExtra(conn,tx,actId,positionId)").Length - 1);
        var acts = Read("GmcSmeta.Core/Db/SqlServerActs.cs");
        var start = acts.IndexOf("public static long AddExtraLine(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = acts.IndexOf("// ---------------------------------------------------------------- внутреннее", start, StringComparison.Ordinal);
        Assert.True(end > start);
        var add = acts[start..end];
        Assert.Contains("BeginTransaction(IsolationLevel.Serializable)", add);
        Assert.Contains("WHERE a.id=@id AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", add);
        Assert.Contains("JOIN dbo.objects o WITH (HOLDLOCK)", add);
    }
}
