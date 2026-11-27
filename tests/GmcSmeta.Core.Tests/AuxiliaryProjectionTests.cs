using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AuxiliaryProjectionTests
{
    [Theory]
    [InlineData("КЛАПАН  п1", "П1: клапан", true)]
    [InlineData("п1\t\nклапан", "КЛАПАН П1", true)]
    [InlineData("п1 клапан", "П1: датчик", false)]
    [InlineData("  ", null, true)]
    [InlineData("клапан", null, false)]
    public void PreparedHistorySearchKeepsOriginalWordSemantics(string query, string? line, bool expected)
    {
        Assert.Equal(expected, HistoryPresentation.MatchesWords(query, line));
        Assert.Equal(expected, HistoryPresentation.MatchesSearchWords(HistoryPresentation.SearchWords(query), line));
    }

    [Fact]
    public void ExcelPreviewReusesReportAndNoOpSelectionDoesNotRefreshGrid()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ExcelImportPreviewWindow.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var import = File.ReadAllText(Path.Combine(app, "ExcelImportPreviewWindow.cs"));
        var update = File.ReadAllText(Path.Combine(app, "ExcelExistingUpdatePreviewWindow.cs"));
        Assert.Contains("if (!ReferenceEquals(_reportRow, row))", import);
        Assert.Contains("var duplicate = ObjectId is long id", import);
        Assert.Contains("if (_report.Text != text) _report.Text = text;", import);
        Assert.Contains("var baseCompany = SelectedBaseCompany();", import);
        Assert.Contains("if (row.Apply == apply) continue;", update);
        Assert.Contains("if (changed) _grid.Items.Refresh();", update);
    }
}
