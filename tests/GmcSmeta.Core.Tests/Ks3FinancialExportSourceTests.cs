namespace GmcSmeta.Core.Tests;

public sealed class Ks3FinancialExportSourceTests
{
    [Fact]
    public void FinancialExportUsesCapturedReportAndCancelableSafeSave()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "Ks3ReportWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var window = File.ReadAllText(Path.Combine(app, "Ks3ReportWindow.xaml.cs"));
        var save = File.ReadAllText(Path.Combine(app, "ContractDocumentSave.cs")).Replace("\r\n", "\n");
        Assert.Contains("await ContractDocumentSave.XlsxAsync", window);
        Assert.Contains("() => CreateWorkbook(report, _actNumber, _actDate), token", window);
        Assert.Contains("private static XLWorkbook CreateWorkbook", window);
        Assert.Contains("sheet.Columns().AdjustToContents(1, Math.Min", window);
        Assert.Contains("_exportLifetime.Cancel();", window);
        Assert.Contains("if (_closed || _exportInProgress || report is null) return;", window);
        Assert.Contains("if (!_closed) ExportButton.IsEnabled = _report is not null;", window);
        Assert.DoesNotContain("XlsxExport.Save(this", window);
        Assert.Contains("using var prepared = ExportFilePublication.Stage(destination, generate);", save);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested();\n                prepared.Publish();", save);
        Assert.Contains("using var check = new XLWorkbook(stream);", save);
        Assert.Contains("create, CancellationToken.None", save);
    }
}
