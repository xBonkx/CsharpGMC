namespace GmcSmeta.Core.Tests;

public sealed class OrdersInitialReadLifecycleTests
{
    [Fact]
    public void InitialReadIsCancellableAndReusesOrderHistory()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "OrdersWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var source = File.ReadAllText(Path.Combine(app, "OrdersWindow.xaml.cs"));
        var ctor = source[source.IndexOf("private OrdersWindow(", StringComparison.Ordinal)..
            source.IndexOf("private async Task InitializeAsync", StringComparison.Ordinal)];
        Assert.Contains("OrderContent.IsEnabled = false;", ctor);
        Assert.Contains("MakeButton.IsEnabled = false;", ctor);
        Assert.Contains("_initialCancellation?.Cancel();", ctor);
        Assert.DoesNotContain("db.History(", ctor);
        var initial = source[source.IndexOf("private async Task InitializeAsync", StringComparison.Ordinal)..
            source.IndexOf("private static long? LastResponsible", StringComparison.Ordinal)];
        Assert.Contains("await Task.Run", initial);
        Assert.Contains("db.Load(false, token)", initial);
        Assert.Contains("db.OrderSite(_objectId, _documentId, token)", initial);
        Assert.Contains("OrderAgreementPolicy.BlockReason(_site, _companies)", initial);
        Assert.Contains("db.Companies(token)", initial);
        Assert.Equal(1, initial.Split("db.History(", StringSplitOptions.None).Length - 1);
        Assert.Contains("LastResponsible(snapshot.History)", initial);
        Assert.Contains("ApplyHistory(snapshot.History);", initial);
        Assert.DoesNotContain("RefreshHistory();", initial);
        Assert.True(initial.IndexOf("if (_closed || token.IsCancellationRequested) return;", StringComparison.Ordinal) <
            initial.IndexOf("_db = snapshot.Db;", StringComparison.Ordinal));
        Assert.Contains("OrderContent.IsEnabled = true;", initial);
        Assert.Contains("if (_closed || _initializing || string.IsNullOrWhiteSpace(_revision)) return;", source);
        foreach (var name in new[] { "MainWindow.Passports.cs", "MainWindow.xaml.cs", "EstimateView.Header.cs", "PeopleAccessWindows.cs" })
            Assert.DoesNotContain("new OrdersWindow(new SqlServerPeopleAccess(", File.ReadAllText(Path.Combine(app, name)));
    }
}
