using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class NavigationReusePolicyTests
{
    private static readonly ObjectRow Obj = new(1, "Объект", "", "", false, "");
    private static DocRow Doc(int id) => new(id, 1, "kp", "КП", "КП", "КП", "", "", false, 1, 0, 1, 1);

    [Fact]
    public void ReusesOnlyExactObjectAndOrderedDocumentValues()
    {
        var before = new NavigationObject(Obj, new[] { Doc(2), Doc(3) });
        Assert.True(NavigationReusePolicy.SameBranch(before,
            new NavigationObject(Obj with { }, new[] { Doc(2), Doc(3) })));
        Assert.False(NavigationReusePolicy.SameBranch(before,
            new NavigationObject(Obj with { Name = "Новое имя" }, before.Documents)));
        Assert.False(NavigationReusePolicy.SameBranch(before,
            new NavigationObject(Obj, new[] { Doc(2) with { Locked = true }, Doc(3) })));
        Assert.False(NavigationReusePolicy.SameBranch(before,
            new NavigationObject(Obj, new[] { Doc(3), Doc(2) })));
        Assert.False(NavigationReusePolicy.SameBranch(before,
            new NavigationObject(Obj, new[] { Doc(2), Doc(3) with { StageOf = 2, StageRest = true } })));
        Assert.False(NavigationReusePolicy.SameBranch(before, new NavigationObject(Obj, new[] { Doc(2) })));
    }

    [Fact]
    public void RemembersCollapsedStageParentAndDefaultsOnlyForUnknownNodes()
    {
        var known = new HashSet<string> { "doc:2", "doc:3" };
        var opened = new HashSet<string> { "doc:3" };
        Assert.False(NavigationReusePolicy.Expanded("doc:2", true, opened, known));
        Assert.True(NavigationReusePolicy.Expanded("doc:3", false, opened, known));
        Assert.True(NavigationReusePolicy.Expanded("doc:4", true, opened, known));
        Assert.False(NavigationReusePolicy.Expanded("obj:5", false, opened, known));
    }
}
