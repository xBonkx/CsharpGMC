using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateUndoRevisionPolicyTests
{
    [Theory]
    [InlineData(null, "rev1", false)]
    [InlineData("", "rev1", false)]
    [InlineData("rev1", "rev1", false)]
    [InlineData("rev1", "rev2", true)]
    [InlineData("rev1", "", true)]
    public void RemoteRevisionsInvalidateLocalUndo(string? previous, string observed, bool expected)
    {
        Assert.Equal(expected, EstimateUndoRevisionPolicy.IsForeignRevision(previous, observed));
    }
}
