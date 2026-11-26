namespace GmcSmeta.Core.Tests;

public sealed class ReportPublicationSourceTests
{
    private static string Source(string file)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", file)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", file));
    }

    [Fact]
    public void SpecificationHistorySortsOnceAndUsesWorkerWithCapturedRows()
    {
        var report = Source("SpecUpdateExcelReport.cs");
        Assert.Contains("var ordered = history.OrderBy(r => r.Id).ToArray();", report);
        Assert.Contains("foreach (var revision in ordered)", report);
        Assert.Contains("var item = ordered[i];", report);
        Assert.DoesNotContain("history.OrderBy(x => x.Id).ElementAt(i)", report);
        Assert.Contains("ExportFilePublication.Write(path, stream => workbook.SaveAs(stream))", report);
        var window = Source("EstimateView.SpecUpdate.cs");
        Assert.Contains("var snapshot = revisions.ToArray();", window);
        Assert.Contains("await Task.Run(() => SpecUpdateExcelReport.Export(destination, snapshot))", window);
        Assert.Contains("if (exporting || closed) return;", window);
        Assert.Contains("if (!closed) export.IsEnabled = revisions.Count > 0;", window);
    }

    [Fact]
    public void LiveRegisterDoesNotBindWorkbookToExportFile()
    {
        var source = Source("MetalRegisterWindow.cs");
        Assert.DoesNotContain("_book.SaveAs(picker.FileName)", source);
        Assert.Contains("ExportFilePublication.WriteBytes(picker.FileName, bytes)", source);
        Assert.Equal(2, source.Split("LiveWorkbookSnapshot.Capture(_book)").Length - 1);
        var helper = Source("LiveWorkbookSnapshot.cs");
        Assert.Contains("var backing = new MemoryStream();", helper);
        Assert.Contains("workbook.SaveAs(backing);", helper);
        Assert.DoesNotContain("using var backing", helper);
    }

    [Fact]
    public void LegacyKsPdfSavePublishesOnlyAfterValidation()
    {
        var source = Source("KsPdfForms.cs");
        Assert.Contains("throw new InvalidDataException(\"PDF не сформирован.\");", source);
        Assert.Contains("ExportFilePublication.WriteBytes(dlg.FileName,pdf);", source);
        Assert.DoesNotContain("File.WriteAllBytes(dlg.FileName,pdf)", source);
    }
}
