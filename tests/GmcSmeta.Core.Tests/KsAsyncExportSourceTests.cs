namespace GmcSmeta.Core.Tests;

public sealed class KsAsyncExportSourceTests
{
    [Fact]
    public void ExportsAwaitWorkerAndPublishOnlyAfterContextCheck()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs"));
        var start = code.IndexOf("private async void ExportAct_Click(", StringComparison.Ordinal);
        var end = code.IndexOf("private void ExportGroup_Click(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var export = code[start..end];
        Assert.DoesNotContain("GetAwaiter().GetResult()", export);
        Assert.Contains("private async void ExportVor_Click(", export);
        Assert.Contains("private async void ExportAll_Click(", export);
        Assert.Contains("await Task.Run(read)", export);
        Assert.Contains("await Task.Run(() => render(snapshot))", export);
        Assert.Contains("await Task.Run(() => File.WriteAllBytes(staged, bytes));\n            RequireExportContext();", export.Replace("\r\n", "\n"));
        Assert.Contains("File.Move(staged, destination, overwrite: true)", export);
        Assert.Contains("attachment == _exportAttachmentGeneration", export);
        Assert.Contains("RequireCurrentActCommand(target);", export);
        Assert.Contains("if (_exportInProgress) return;", export);
        Assert.Contains("File.Delete(staged)", export);
    }

    [Fact]
    public void GroupExportsStageInBackgroundAndGuardPublication()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "KsGroupReportWindow.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "KsGroupReportWindow.xaml.cs"))
            .Replace("\r\n", "\n");
        Assert.Contains("private async void Excel_Click(", code);
        Assert.Contains("private async void Pdf_Click(", code);
        Assert.Contains("private async void Vor_Click(", code);
        Assert.Contains("KsCustomerForms.Create(p)", code);
        Assert.Contains("KsPdfForms.Create(p)", code);
        Assert.Contains("KsVorForms.Create(p, act.Id)", code);
        Assert.Contains("if (_closed || _exportInProgress) return;", code);
        Assert.Contains("await Task.Run(() => render(staged));", code);
        var check = code.IndexOf("if (_closed) return;", code.IndexOf("await Task.Run(() => render(staged));", StringComparison.Ordinal), StringComparison.Ordinal);
        var publish = code.IndexOf("File.Move(staged, destination, overwrite: true)", StringComparison.Ordinal);
        Assert.True(check >= 0 && publish > check);
        Assert.Contains("File.Delete(staged)", code);
        Assert.Contains("if (!_closed) SetExportButtons(_package is not null);", code);
        Assert.DoesNotContain("XlsxExport.SaveCreatedWorkbook(", code);
        Assert.DoesNotContain("KsPdfForms.Save(", code);
    }
}
