namespace GmcSmeta.Core.Tests;

public sealed class CostSummaryReadCancellationSourceTests
{
    [Fact]
    public void DetachedOrSupersededSummaryCannotPublishAndCancellationDoesNotTouchWrites()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "CostSummaryPane.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var code = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "CostSummaryPane.xaml.cs"));
        Assert.Equal(2, code.Split("_costReadCancellation?.Cancel();", StringSplitOptions.None).Length - 1);
        Assert.Contains("ReadCostSnapshot(_connectionString, _contractId, token), token", code);
        var read = code.IndexOf("private static CostLoadSnapshot ReadCostSnapshot", StringComparison.Ordinal);
        var load = code.IndexOf("private async Task ReloadAsync()", StringComparison.Ordinal);
        Assert.True(read >= 0 && load > read);
        Assert.True(code[read..load].Split("token.ThrowIfCancellationRequested();", StringSplitOptions.None).Length >= 7);
        Assert.Contains("if (token.IsCancellationRequested || !_attached || generation != _loadGeneration) return;", code);
        Assert.Contains("if (ReferenceEquals(_costReadCancellation, cancellation)) _costReadCancellation = null;", code);
        Assert.Contains("owner.ReloadAsync(); // includes failed/unknown COMMIT; never replay the write", code);
    }
}
