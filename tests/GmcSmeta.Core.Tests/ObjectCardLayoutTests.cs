using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class ObjectCardLayoutTests
{
    [Theory]
    [InlineData(0, 318, 1, 288)]
    [InlineData(691, 1000, 1, 679)]
    [InlineData(692, 1000, 2, 334)]
    [InlineData(1044, 1000, 3, 336)]
    [InlineData(2000, 1000, 3, 654)]
    public void CardWidthPreservesViewportAndColumnThresholds(double viewport, double actual, int columns, double width)
    {
        Assert.Equal((columns,width),ObjectCardLayout.Measure(viewport,actual));
    }

    [Fact]
    public void RestoredGeometryClampsToWorkAreaOrKeepsDefaultWhenTooSmall()
    {
        Assert.Equal((1200d,800d),ObjectCardLayout.RestoreGeometry(1800,1000,1200,800,600,400));
        Assert.Null(ObjectCardLayout.RestoreGeometry(1800,1000,500,800,600,400));
        Assert.Null(ObjectCardLayout.RestoreGeometry(1800,300,1200,800,600,400));
    }

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
