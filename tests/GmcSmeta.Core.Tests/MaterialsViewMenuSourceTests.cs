namespace GmcSmeta.Core.Tests;

public sealed class MaterialsViewMenuSourceTests
{
    [Fact]
    public void ExplicitViewChoiceRefreshesFrozenKeysAndUsesPythonResetIcon()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.Materials.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var code = File.ReadAllText(Path.Combine(app, "EstimateView.Materials.cs"));
        var start = code.IndexOf("private void MaterialsSetView(", StringComparison.Ordinal);
        var end = code.IndexOf("private void MaterialsGrouping_Click(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = code[start..end];
        Assert.Contains("_materialsFrozenKeys.Clear();", method);
        Assert.Contains("_materialsFrozenDoc = null;", method);
        Assert.Contains("MaterialsRebuild();", method);
        Assert.Contains("_materialsFrozenKeys.TryGetValue(p.Id, out var frozen)", code);
        Assert.Contains("data.Positions.ToLookup(p => p.SystemId)", code);
        Assert.Contains("_materialsTop.ToLookup(p => p.SystemId)", code);
        Assert.Contains("MaterialsVisibleRows.SameReferences(existing, result)", code);
        var grouped = File.ReadAllText(Path.Combine(app, "EstimateView.Groups.cs"));
        Assert.DoesNotContain("materialsGrid.ItemsSource", grouped);
        Assert.DoesNotContain("MaterialsGrid.ItemsSource", grouped);
        var stages = File.ReadAllText(Path.Combine(app, "EstimateView.InlineStages.cs"));
        Assert.Contains("StageFilterAncestors.Mark(rows.Count,", stages);
        Assert.Contains("if (_inlineStagesFilter == filter) return;", stages);
        Assert.Contains("row.HasStagePresentation(_inlineStagesVersion)", stages);
        Assert.Contains("_inlineStageColumnKeys.SequenceEqual(keys)", stages);
        Assert.Contains("if (!keep[i]) continue;", stages);
        Assert.Contains("FillInlineStageCells(rows[i]);", stages);
        Assert.Contains("StageAllocationPresentation.Summarize(data.Stages.Count,", stages);
        Assert.Contains("if (MaterialsGrid is null || _materialsUpdatingSystemFilter) return;", code);
        Assert.Contains("ContextMenuIcons.Create(\"Вернуть как в КП\", \"X\")", code);
        var icons = File.ReadAllText(Path.Combine(app, "Theme", "ToolbarIcons.xaml"));
        Assert.Contains("x:Key=\"GlyphX\" Figures=\"M6 6l12 12M18 6 6 18\"", icons);
    }
}
