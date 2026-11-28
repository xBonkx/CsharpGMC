namespace GmcSmeta.Core.Tests;

public sealed class LayoutInteractionSourceTests
{
    [Theory]
    [InlineData("tests/GmcSmeta.App.Checks/Program.cs", "--layout-interaction-check")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "BackgroundEstimateChecks.LoadEstimateGrid()")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "CompositionTarget.Rendering += Render")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "CompositionTarget.Rendering -= Render")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "private const int Measured = 120")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "VisualTreeHelper.GetDpi(host)")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "p99Ms = P(.99, 100)")]
    [InlineData("tests/GmcSmeta.App.Checks/LayoutInteractionChecks.cs", "Timed out waiting for rendering samples.")]
    public void OfflineHarnessKeepsActualRenderAndDpiEvidence(string file, string marker)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, file))) root = root.Parent;
        Assert.NotNull(root);
        Assert.Contains(marker, File.ReadAllText(Path.Combine(root!.FullName, file)));
    }
}
