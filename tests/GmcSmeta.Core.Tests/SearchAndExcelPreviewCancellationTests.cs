using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class SearchAndExcelPreviewCancellationTests
{
    private static string Source(string path)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", path)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", path));
    }

    [Fact]
    public void SearchSerializesRequestsAndRejectsLateOrNonRowOpen()
    {
        var code = Source("GmcSmeta.App/WorkspaceQueryWindows.cs");
        var search = code[code.IndexOf("public sealed class GlobalSearchWindow", StringComparison.Ordinal)..
            code.IndexOf("/// <summary>Catalog manage mode", StringComparison.Ordinal)];
        Assert.Contains("await _searchGate.WaitAsync(ct)", search);
        Assert.Contains("finally { _searchGate.Release(); }", search);
        Assert.Contains("if (_closed || request != _request || ct.IsCancellationRequested) return;", search);
        Assert.Contains("ReferenceEquals(_searchCancellation, cancellation)", search);
        Assert.DoesNotContain("_searchCancellation?.Dispose()", search);
        Assert.Contains("ItemsControl.ContainerFromElement(_results, source) is ListBoxItem item", search);
    }

    [Fact]
    public void CancelledPreviewStopsBeforeSqlOrHashing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var token = cancellation.Token;
        Assert.Throws<OperationCanceledException>(() => SqlServerExcelExistingUpdate.Read("invalid connection", 1, token));
        Assert.Throws<OperationCanceledException>(() => SqlServerExcelExistingUpdate.Prepare(
            "invalid connection", 1, "missing.xlsx", Array.Empty<ExcelImportBlock>(), token));
        Assert.Throws<OperationCanceledException>(() => ExcelExistingUpdatePlan.Flatten(Array.Empty<ExcelImportBlock>(), token));
        Assert.Throws<OperationCanceledException>(() => ExcelExistingUpdatePlan.Build(
            1, "r", new string('a', 64), Array.Empty<ExcelExistingDbRow>(),
            Array.Empty<ExcelExistingSourceRow>(), Array.Empty<string>(), token: token));
    }

    [Fact]
    public void PreviewCloseCancelsReadAndSelectionFencePrecedesPublication()
    {
        var ui = Source("GmcSmeta.App/ExcelExistingUpdatePreviewWindow.cs");
        Assert.Contains("_previewCancellation?.Cancel();", ui);
        Assert.Contains("new[] { block.Block }, token)", ui);
        var guard = ui.IndexOf("!ReferenceEquals(_contracts.SelectedItem, contract)", StringComparison.Ordinal);
        var publish = ui.IndexOf("PublishPlan(result.Plan, result.HasSafe);", StringComparison.Ordinal);
        Assert.True(guard >= 0 && publish > guard);
        var sql = Source("GmcSmeta.Core/Db/SqlServerExcelExistingUpdate.cs");
        Assert.Contains("SqlReadCancellation.Open(connectionString, token)", sql);
        Assert.Contains("SqlReadCancellation.Scalar(cmd, token)", sql);
        Assert.Contains("SHA256.HashDataAsync(stream, token)", sql);
        Assert.Contains("ExcelExistingUpdatePlan.Flatten(blocks, token)", sql);
        Assert.Contains("plan.MappingCandidates(source.Ordinal, token)", ui);
        Assert.Contains("plan.WithExplicitMapping(source.Ordinal, old.Id, token)", ui);
        Assert.Contains("mappingCancellation?.Cancel();", ui);
        var import = Source("GmcSmeta.App/ExcelImportPreviewWindow.cs");
        Assert.Contains("await _reportGate.WaitAsync(token)", import);
        Assert.Contains("!ReferenceEquals(_table.SelectedItem, row)", import);
        Assert.Contains("_reportCancellation?.Cancel();", import);
        var stage = Source("GmcSmeta.App/StagesDialog.xaml.cs");
        Assert.Contains("if (_distributionCancellation is not null) return;", stage);
    }
}
