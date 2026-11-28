using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateNestedVisibilityTests
{
    [Theory]
    [InlineData(false, false, false, false, true, true)]
    [InlineData(false, false, true, false, true, false)]
    [InlineData(false, false, true, true, true, true)]
    [InlineData(true, false, false, false, true, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(true, true, true, false, true, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, false, false, false, false, true)]
    public void FoldsOrdinaryAndAreaComponentsWithoutHidingFilteredMatches(
        bool area, bool expanded, bool collapse, bool reveal, bool honorArea, bool expected) =>
        Assert.Equal(expected, EstimateNestedVisibility.ShowComponents(area, expanded, collapse, reveal, honorArea));

    [Fact]
    public void BothEstimateProjectionsUseSharedPolicyAndViewCommandsDoNotWriteSql()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App")))
            root = root.Parent;
        Assert.NotNull(root);
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", path));
        var view = Read("EstimateView.TemporaryView.cs");
        Assert.Contains("_ordinaryComponentsCollapsed = true;", view);
        Assert.Contains("_ordinaryComponentsCollapsed = false;", view);
        Assert.Contains("if (!CanChangeEstimateView()) return;", view);
        Assert.Contains("AddViewCommand(menu, \"Развернуть всё\", \"Indent\", ExpandAllRows)", view);
        Assert.Contains("Как группировать", view);
        Assert.Contains("Как сортировать", view);
        Assert.Contains("Сортировка — один раз, как в Excel", view);
        Assert.DoesNotContain("SqlServer", view);
        Assert.Contains("EstimateNestedVisibility.ShowComponents", Read("EstimateView.xaml.cs"));
        Assert.Contains("EstimateNestedVisibility.ShowComponents", Read("EstimateView.Groups.cs"));
        var materials = Read("EstimateView.Materials.cs");
        Assert.Contains("StyleBlue(view.MaterialsGroupingButton, _materialsMode != \"kp\")", materials);
        Assert.Contains("MaterialsGrid is { } grid && HasGridEdit(grid)", materials);
    }
}
