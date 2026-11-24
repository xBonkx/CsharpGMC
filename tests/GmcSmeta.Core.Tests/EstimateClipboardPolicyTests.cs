using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateClipboardPolicyTests
{
    [Theory]
    [InlineData(true, "position\tqty", "position\tqty", true)]
    [InlineData(true, "position\tqty", "other Excel data", false)]
    [InlineData(false, "position\tqty", "position\tqty", false)]
    [InlineData(true, null, "position\tqty", false)]
    public void OnlyCurrentWindowsClipboardCanUseInternalRows(
        bool copied, string? owned, string actual, bool expected)
    {
        Assert.Equal(expected, EstimateClipboardPolicy.IsOwned(copied, owned, actual));
    }
}
