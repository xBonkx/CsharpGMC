using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ColumnOrderPolicyTests
{
    [Fact]
    public void FixedSlotsSurviveDragAndRestore()
    {
        string[] defaults = ["number", "name", "mode", "qty", "price", "basis"];
        var fixedIds = new HashSet<string> { "number", "mode", "basis" };
        Assert.Equal(new[] { "number", "price", "mode", "qty", "name", "basis" },
            ColumnOrderPolicy.Normalize(defaults, ["basis", "price", "mode", "qty", "name", "number"], fixedIds));
    }

    [Fact]
    public void UnknownDuplicateAndAbsentIdsDoNotDisplaceNewColumns()
    {
        string[] defaults = ["number", "name", "qty", "new", "basis"];
        Assert.Equal(new[] { "number", "qty", "name", "new", "basis" },
            ColumnOrderPolicy.Normalize(defaults, ["old", "qty", "qty", "basis"], new HashSet<string> { "number", "basis" }));
    }

    [Fact]
    public void ResetAndAllFixedKeepDefaultOrder()
    {
        string[] defaults = ["a", "b", "c"];
        Assert.Equal(defaults, ColumnOrderPolicy.Normalize(defaults, [], new HashSet<string> { "a" }));
        Assert.Equal(defaults, ColumnOrderPolicy.Normalize(defaults, ["c", "b", "a"], defaults.ToHashSet()));
        Assert.Empty(ColumnOrderPolicy.Normalize([], ["stale"], new HashSet<string>()));
    }

    [Fact]
    public void DuplicateDefaultIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ColumnOrderPolicy.Normalize(["a", "a"], [], new HashSet<string>()));
    }
}
