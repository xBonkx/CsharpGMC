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
}
