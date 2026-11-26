namespace GmcSmeta.Core.Tests;

public sealed class OrderIssueLifecycleTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Fact]
    public void WindowCapturesRequestAndBlocksCloseUntilWorkerFinishes()
    {
        var source = Source("OrdersWindow.xaml.cs");
        var issue = source[source.IndexOf("private async void Make_Click", StringComparison.Ordinal)..
            source.IndexOf("private void Error(", StringComparison.Ordinal)];
        Assert.Contains("Closing += (_, e) => { if (_issuing) e.Cancel = true; };", source);
        Assert.Contains("if (_issuing) return;", issue);
        Assert.Contains("if (_historyOpenCancellation is not null) return;", issue);
        Assert.Contains("OrderContent.IsEnabled = false;", issue);
        Assert.Contains("CloseButton.IsEnabled = false;", issue);
        var worker = issue.IndexOf("await Task.Run(() => OrderIssueService.Issue(_db, request))", StringComparison.Ordinal);
        Assert.True(worker > issue.IndexOf("new OrderIssueService.Request(", StringComparison.Ordinal));
        Assert.True(worker > issue.IndexOf("_issuing = true;", StringComparison.Ordinal));
        Assert.Contains("if (!result.Registered)", issue);
        Assert.Contains("_registrationUncertain = true;", issue);
        Assert.Contains("_revision = result.Revision ?? "+(char)34+(char)34+";", issue);
        Assert.Contains("MakeButton.IsEnabled = !_registrationUncertain && _revision.Length > 0", issue);
        Assert.Contains("finally", issue);
        Assert.DoesNotContain("RegisterExport(", issue);
    }

    [Fact]
    public void WorkerNeverDeletesOrRetriesAfterRegistrationStarts()
    {
        var source = Source("OrderIssueService.cs");
        Assert.DoesNotContain("System.Windows", source);
        var register = source.IndexOf("db.RegisterExport(", StringComparison.Ordinal);
        Assert.True(register > source.IndexOf("PeopleDocuments.FillDocx(", StringComparison.Ordinal));
        Assert.True(register > source.IndexOf("File.Delete(path)", StringComparison.Ordinal));
        var after = source[register..];
        Assert.DoesNotContain("File.Delete(", after);
        Assert.Equal(1, source.Split("db.RegisterExport(", StringSplitOptions.None).Length - 1);
        Assert.Contains("db.WasRegistered(request.ObjectId, request.Operation)", after);
        Assert.Contains("new Result(false, generated.Count, null, null, ex.Message)", after);
        Assert.Contains("new Result(true, generated.Count, null, history,", after);
        Assert.Contains("new Result(true, generated.Count, null, null,", after);
        Assert.Contains("db.History(request.ObjectId, "+(char)34+"order"+(char)34+")", after);
    }
}
