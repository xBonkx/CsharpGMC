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
        Assert.Contains("_materialsOrder.Reset();", method);
        Assert.Contains("MaterialsRebuild();", method);
        var order = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.Core", "Domain", "MaterialsOrder.cs"));
        Assert.Contains("_materialsFrozenKeys.TryGetValue(p.Id, out var frozen)", order);
        Assert.Contains("_materialsFrozenDoc = null;", order);
        Assert.Contains("_materialsOrder.Order(_contractId, _materialsMode, _materialsSort, _materialsTop, MatRowColors)", code);
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
        Assert.Contains("StageAllocationPresentation.Summarize(stages.Count,", stages);
        var temporary = File.ReadAllText(Path.Combine(app, "EstimateView.TemporaryView.cs"));
        Assert.Contains("ApplyFilter(expandAllGroups: true);", temporary);
        Assert.DoesNotContain("EstimateGroups.Build(", temporary);
        Assert.Contains("if (expandAllGroups)", grouped);
        var main = File.ReadAllText(Path.Combine(app, "EstimateView.xaml.cs"));
        Assert.Contains("ApplyGroupedFilter(expandAllGroups)", main);
        Assert.Contains("RowViewIndex.FindSection(systemId)", main);
        Assert.Contains("!ReferenceEquals(EstimateGrid.SelectedItem, selected)", main);
        Assert.Contains("if (MaterialsGrid is null || _materialsUpdatingSystemFilter) return;", code);
        Assert.Contains("ContextMenuIcons.Create(\"Вернуть как в КП\", \"X\")", code);
        var icons = File.ReadAllText(Path.Combine(app, "Theme", "ToolbarIcons.xaml"));
        Assert.Contains("x:Key=\"GlyphX\" Figures=\"M6 6l12 12M18 6 6 18\"", icons);
    }
    [Fact]
    public void MaterialsHierarchyIsViewOnlyAndReportDoesNotDuplicateMembers()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "EstimateView.MaterialsHierarchy.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root!.FullName, "src", "GmcSmeta.App");
        var hierarchy = File.ReadAllText(Path.Combine(app, "EstimateView.MaterialsHierarchy.cs"));
        Assert.Contains("HasGridEdit(grid)", hierarchy);
        Assert.Contains("Items.Contains(row)", hierarchy);
        Assert.DoesNotContain("SqlServer", hierarchy);
        var view = File.ReadAllText(Path.Combine(app, "EstimateView.Materials.cs"));
        Assert.Contains("MaterialsMergedRows(members, labels, header)", view);
        Assert.Contains("MaterialsPositionRow(p, \"\", labels.GetValueOrDefault(p.SystemId), group)", view);
        Assert.Contains("export: true", view);
        Assert.Contains("if (row.IsSystemHeader) { e.Cancel = true; return; }", view);
        var projection = File.ReadAllText(Path.Combine(app, "MaterialsVisibleRows.cs"));
        Assert.Contains("eligible[e.Parent] = true", projection);
        Assert.Contains("export ? parent.IsGroup && !parent.IsSystemHeader : !parent.IsExpanded", projection);
    }
}
