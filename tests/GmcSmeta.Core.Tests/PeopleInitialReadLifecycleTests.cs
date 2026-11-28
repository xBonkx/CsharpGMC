using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class PeopleInitialReadLifecycleTests
{
    [Fact]
    public void InitialReadRunsOnWorkerAndPublishesOnlyWhileWindowIsOpen()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs"));
        var ctor = source[source.IndexOf("public PeopleView(", StringComparison.Ordinal)..source.IndexOf("private async Task InitializeAsync", StringComparison.Ordinal)];
        Assert.DoesNotContain("new SqlServerPeopleAccess(", ctor);
        Assert.DoesNotContain("Reload();", ctor);
        Assert.Contains("content.IsEnabled = false;", ctor);
        Assert.Contains("await InitializeAsync(connectionString, content)", ctor);
        Assert.Contains("_initialCancellation?.Cancel();", ctor);
        Assert.Contains("var snapshot = await Task.Run", source);
        Assert.Contains("var db = new SqlServerPeopleAccess(connectionString, token);", source);
        Assert.Contains("db.Site(objectId, token)", source);
        Assert.Contains("db.Companies(token)", source);
        Assert.Contains("db.History(id, \"list\", token)", source);
        Assert.Contains("var directory = db.Load(showInactive, token);", source);
        Assert.Contains("if (_closed || token.IsCancellationRequested) return;", source);
        Assert.Contains("ApplyHistory(snapshot.History);", source);
        Assert.Contains("ApplyDirectory(snapshot.Directory.People, snapshot.Directory.Cars, snapshot.Directory.Revision, null, null);", source);
        Assert.Contains("content.IsEnabled = true;", source);
        Assert.Contains("if (_closed || _loading || _operationBusy) return;", source);
        Assert.Contains("finally { _loading = wasLoading; }", source);
    }
    [Fact]
    public void AccessHistorySqlQuotesReservedFileIdentifier()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerPeopleAccess.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Db", "SqlServerPeopleAccess.cs"));
        Assert.Contains("a.created_at,a.[file],a.[user],a.[data]", source);
        Assert.DoesNotContain("a.file", source);
        Assert.Contains("INSERT dbo.access_lists(object_id,kind,created_at,[user],[file],[data])", source);
    }

    [Fact]
    public void CancelledServiceConstructionDoesNotConnect()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SqlServerPeopleAccess(
            "Server=invalid;Database=GmcSmetaPilot_People;Integrated Security=true", cancellation.Token));
    }

    [Fact]
    public void CancellationDoesNotBypassPilotGuard()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<InvalidOperationException>(() => new SqlServerPeopleAccess(
            "Server=invalid;Database=GmcSmeta;Integrated Security=true", cancellation.Token));
    }

    [Fact]
    public void DirectorySearchUsesAuthoritativeSnapshotAndRejectsMissingRevision()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs"));
        var apply = source[source.IndexOf("private void ApplyDirectory", StringComparison.Ordinal)..source.IndexOf("private void UpdateCounts", StringComparison.Ordinal)];
        Assert.Contains("string.IsNullOrWhiteSpace(revision)", apply);
        Assert.Contains("_peopleSearch = _people.ToDictionary", apply);
        Assert.Contains("_carSearch = _cars.ToDictionary", apply);
        var filter = apply[apply.IndexOf("private void ApplyFilter", StringComparison.Ordinal)..];
        Assert.Contains("_peopleSearch.TryGetValue(p.Id", filter);
        Assert.Contains("_carSearch.TryGetValue(c.Id", filter);
        Assert.DoesNotContain("p.Fio +", filter);
        Assert.DoesNotContain("c.Model +", filter);
        Assert.Contains("StringComparison.CurrentCultureIgnoreCase", filter);
    }

    [Fact]
    public void ReadOnlyRefreshPreservesSelectionAndGuardsWritesUntilReadback()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "PeopleWindow.xaml.cs"));
        var reload = source[source.IndexOf("private async Task ReloadReadOnlyAsync", StringComparison.Ordinal)..
            source.IndexOf("private void ApplyDirectory", StringComparison.Ordinal)];
        Assert.Contains("RememberChecks();", reload);
        Assert.Contains("_revision = \"\";", reload);
        Assert.Contains("content.IsEnabled = false;", reload);
        Assert.Contains("await Task.Run(() => _db.Load(showInactive, token), token)", reload);
        Assert.Contains("if (_closed || token.IsCancellationRequested) return;", reload);
        Assert.Contains("if (history is not null) RestoreHistory(history);", reload);
        Assert.True(reload.IndexOf("RestoreHistory(history)", StringComparison.Ordinal) <
            reload.IndexOf("ApplyDirectory(data.People", StringComparison.Ordinal));
        Assert.Contains("ApplyDirectory(data.People, data.Cars, data.Revision, selectedP, selectedC);", reload);
        Assert.Contains("if (!_closed) content.IsEnabled = true;", reload);
        Assert.Contains("_reloadCancellation?.Cancel();", source);
        Assert.Contains("await ReloadReadOnlyAsync();", source);
        Assert.Contains("await ReloadReadOnlyAsync(option.History);", source);
        Assert.Contains("if (string.IsNullOrWhiteSpace(_revision))", source);
        Assert.Contains("private async Task ReloadMutationAsync", source);
    }

}
