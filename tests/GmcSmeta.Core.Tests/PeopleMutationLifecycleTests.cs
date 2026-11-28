namespace GmcSmeta.Core.Tests;

public sealed class PeopleMutationLifecycleTests
{
    private static string Source()
    {
        DirectoryInfo? root=new(AppContext.BaseDirectory);
        while(root is not null&&!File.Exists(Path.Combine(root.FullName,"src","GmcSmeta.App","PeopleWindow.xaml.cs")))root=root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName,"src","GmcSmeta.App","PeopleWindow.xaml.cs"));
    }

    [Fact]
    public void MutationsUseCapturedRevisionAndGuardWindowUntilReadback()
    {
        var code=Source();
        Assert.Contains("if (_closed || _loading || _operationBusy) return;",code);
        Assert.Contains("if (_operationBusy || _inlinePending) return false;",code);
        Assert.Contains("return _loading || (CommitChecks() && !_inlinePending);",code);
        Assert.Contains("return await Task.Run(() => mutation(revision));",code);
        Assert.Contains("await Task.Run(() => _db.Load(showInactive))",code);
        Assert.Contains("await MutateAsync(revision => _db.SavePerson(row, revision))",code);
        Assert.Contains("await MutateAsync(revision => _db.SaveCar(row, revision))",code);
        Assert.Contains("await MutateAsync(expected => _db.SetActiveBatch",code);
        Assert.Contains("await MutateAsync(expected => _db.DeleteOrDeactivateBatch",code);
        Assert.Contains("await MutateAsync(revision => _db.Reorder(person, ids, revision))",code);
        Assert.DoesNotContain("private void Reload(",code);
    }

    [Fact]
    public void ImportReadsWorkbookOnWorkerAndAppliesChecksAfterSingleReadback()
    {
        var code=Source();
        var start=code.IndexOf("private async void Import_Click",StringComparison.Ordinal);
        var end=code.IndexOf("private async Task RefreshHistoryAsync",start,StringComparison.Ordinal);
        Assert.True(start>=0&&end>start);
        var import=code[start..end];
        Assert.Contains("await Task.Run(() => PeopleDocuments.ReadImport(path))",import);
        Assert.Contains("await MutateAsync(revision => _db.Import(preview, revision))",import);
        Assert.Equal(1,import.Split("await ReloadMutationAsync()").Length-1);
        Assert.Contains("p.Selected = _checkedPeople.Contains(p.Id)",import);
    }

    [Fact]
    public void AccessExportRetainsValidationAndUnknownRegistrationGuard()
    {
        var code=Source();
        Assert.Contains("PeopleDocuments.ExportAccessList(path, company, req, intro, people, cars, signer)",code);
        Assert.Contains("_db.Site(objectId) != site || !_db.Companies().SequenceEqual(companies)",code);
        Assert.Contains("await MutateAsync(expected => _db.RegisterExport",code);
        Assert.Contains("_registrationUncertain = true;",code);
        Assert.Contains("await RefreshHistoryAsync(registeredId);",code);
        Assert.Contains("registeredId = registered.Id;",code);
        Assert.Contains("ApplyHistory(history, registeredId);",code);
        Assert.Contains("h.History.Id == registeredId.Value",code);
        Assert.Contains("if (!history.Any(h => h.Id == registeredId))",code);
        Assert.Contains("CommitChecks()",code);
    }
}
