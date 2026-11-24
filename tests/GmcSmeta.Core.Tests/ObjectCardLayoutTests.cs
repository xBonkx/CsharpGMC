using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class ObjectCardLayoutTests
{
    [Fact]
    public void EmptyObject_StillOffersNewKpCard()
    {
        var cards = ObjectCardLayout.AppendNewContract(Array.Empty<string>(), canCreate: true);

        var create = Assert.IsType<NewContractCardItem>(Assert.Single(cards));
        Assert.True(create.CanCreate);
    }

    [Fact]
    public void ExistingCards_KeepOrder_AndCreationCardIsLast()
    {
        var first = new object();
        var second = new object();
        var original = new[] { first, second };

        var items = ObjectCardLayout.AppendNewContract(original, canCreate: true);

        Assert.Equal(3, items.Count);
        Assert.Same(first, items[0]);
        Assert.Same(second, items[1]);
        Assert.IsType<NewContractCardItem>(items[2]);
        Assert.Equal(2, original.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreationPermission_FollowsObjectWritePermission(bool allowed)
    {
        var cards = ObjectCardLayout.AppendNewContract(Array.Empty<string>(), allowed);
        var create = Assert.IsType<NewContractCardItem>(cards[^1]);

        Assert.Equal(allowed, create.CanCreate);
    }

    [Fact]
    public void NullSource_IsRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            ObjectCardLayout.AppendNewContract<string>(null!, canCreate: true));
    }

    [Fact]
    public void InvalidSourceEntry_IsRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ObjectCardLayout.AppendNewContract(new string[] { null! }, canCreate: true));
    }
}
