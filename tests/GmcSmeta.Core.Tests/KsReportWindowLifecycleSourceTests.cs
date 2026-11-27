namespace GmcSmeta.Core.Tests;

public sealed class KsReportWindowLifecycleSourceTests
{
    private static string ReadApp(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file))
            .Replace("\r\n", "\n");
    }

    [Fact]
    public void RequisitesAreNotEditableBeforeSnapshotOrDuringSave()
    {
        var markup = ReadApp("KsRequisitesWindow.xaml");
        var code = ReadApp("KsRequisitesWindow.xaml.cs");
        Assert.Contains("x:Name=\"RequisitesFields\" IsEnabled=\"False\"", markup);
        Assert.Contains("if (_closed || !IsLoaded) return;", code);
        Assert.Contains("_saving = true;\n        RequisitesFields.IsEnabled = false;", code);
        Assert.Contains("if (_closed || _saving || _loaded is null", code);
        Assert.Contains("RequisitesFields.IsEnabled = !string.IsNullOrWhiteSpace(_revision);", code);
        Assert.Contains("if (!result.Saved)\n            {\n                _revision = \"\";", code);
        Assert.Contains("if (_saving)\n        {\n            e.Cancel = true;", code);
    }

    [Fact]
    public void FinancialOverviewRetainsPeriodAndSkipsQueuedReadAfterClose()
    {
        var code = ReadApp("Ks3ReportWindow.xaml.cs");
        Assert.Contains("период с {from:dd.MM.yyyy} по {to:dd.MM.yyyy}", code);
        Assert.Equal(2, code.Split("ActInfoText.Text = _actInfo;", StringSplitOptions.None).Length - 1);
        Assert.Contains("SqlServerActs.Ks3Figures(_connectionString, _actId), token)", code);
        Assert.Contains("if (_closed || !IsLoaded) return;", code);
    }

    [Fact]
    public void GroupOverviewCancelsQueuedReadAndPublication()
    {
        var code = ReadApp("KsGroupReportWindow.xaml.cs");
        Assert.Contains("_connectionString,_contractId,_groupKey), token);", code);
        Assert.Contains("token.ThrowIfCancellationRequested();\n                prepared.Publish();", code);
        Assert.Contains("await Task.Run(prepared.Dispose);", code);
        Assert.Contains("_lifetime.Cancel();", code);
    }
}
