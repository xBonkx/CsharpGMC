using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Tests;

public sealed class AgreementEditPolicyTests
{
    [Theory]
    [InlineData("kp","main",10L,10L,true,null,null)]
    [InlineData("main","main",10L,null,false,null,10L)]
    [InlineData("main","main",10L,20L,false,null,20L)]
    [InlineData("main","kp",null,20L,true,null,20L)]
    [InlineData("dop","dop",10L,null,true,20L,20L)]
    [InlineData("dop","dop",10L,null,false,null,10L)]
    [InlineData("dop","dop",null,null,false,null,null)]
    public void ResolvesExplicitIdentityWithoutGuessing(string kind,string oldKind,long? previous,
        long? preferred,bool changed,long? inherited,long? expected)
        => Assert.Equal(expected, AgreementEditPolicy.ResolveRequested(kind,oldKind,previous,preferred,changed,inherited));

    [Theory]
    [InlineData(null,null,null)]
    [InlineData(null,10L,10L)]
    [InlineData(10L,10L,10L)]
    public void StageAndDsInheritExactParentIncludingNull(long? preferred,long? parent,long? expected)
        => Assert.Equal(expected, AgreementEditPolicy.InheritParent(preferred,parent));

    [Fact]
    public void CannotAssignUnlinkedOrDifferentParentByGuess()
    {
        Assert.Throws<ArgumentException>(()=>AgreementEditPolicy.InheritParent(10,null));
        Assert.Throws<ArgumentException>(()=>AgreementEditPolicy.InheritParent(10,20));
    }

    [Fact]
    public void RejectsMismatchedOrUnlinkedNewBase()
    {
        Assert.Throws<ArgumentException>(() => AgreementEditPolicy.ResolveRequested("dop","dop",10,20,true,10));
        Assert.Throws<InvalidOperationException>(() => AgreementEditPolicy.ResolveRequested("dop","kp",null,null,true,null));
    }
}
