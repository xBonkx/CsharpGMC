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
        var ctor = source[source.IndexOf("public PeopleWindow(", StringComparison.Ordinal)..source.IndexOf("private async Task InitializeAsync", StringComparison.Ordinal)];
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
        Assert.Contains("if (_closed || _loading) return;", source);
        Assert.Contains("finally { _loading = wasLoading; }", source);
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

}
