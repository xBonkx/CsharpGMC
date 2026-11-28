namespace GmcSmeta.Core.Tests;

public sealed class FunctionalConfirmationDefaultsTests
{
    private static string Source(string name)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", name)))
            root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", name));
    }

    [Theory]
    [InlineData("EstimateView.QueueSave.cs")]
    [InlineData("CostSummaryPane.Smr.cs")]
    [InlineData("CostSummaryPane.Materials.cs")]
    [InlineData("WorkspaceQueryWindows.cs")]
    [InlineData("MetalWarehouseWindow.xaml.cs")]
    [InlineData("MetalRegisterWindow.cs")]
    [InlineData("PassportsWindow.Operations.cs")]
    [InlineData("EstimateView.CostSummary.cs")]
    [InlineData("ExcelExistingUpdatePreviewWindow.cs")]
    public void ReviewedYesNoConfirmationsCannotDefaultToYes(string name)
    {
        var source = Source(name);
        Assert.DoesNotMatch(@"MessageBoxButton\.YesNo,\s*MessageBoxImage\.(?:Question|Warning)\)", source);
        Assert.Contains("MessageBoxResult.No", source);
    }

    [Theory]
    [InlineData("CostSummaryPane.Materials.Actions.cs")]
    [InlineData("CostSummaryPane.Materials.cs")]
    public void DestructiveMaterialChoiceDefaultsToCancel(string name)
    {
        var source = Source(name);
        Assert.DoesNotMatch(@"MessageBoxButton\.YesNoCancel,\s*MessageBoxImage\.(?:Question|Warning)\)", source);
        Assert.Contains("MessageBoxResult.Cancel", source);
    }

    [Fact]
    public void PassportDeletionUsesConfirmedIdentityAndRevision()
    {
        var source = Source("PassportsWindow.Operations.cs");
        var start = source.IndexOf("private async void Delete_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async void ApplyCommon_Click", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.Contains("var revision = p.Revision;", method);
        Assert.Contains("!ReferenceEquals(_rows[index], p)", method);
        Assert.Contains("_sql.Delete(_objectId, id, revision)", method);
        Assert.Contains("_rows.RemoveAt(index)", method);
        Assert.DoesNotContain("_rows.RemoveAt(_current)", method);
    }
}
