using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class AreaTests
{
    [Fact]
    public void Sections_RectAndRound()
    {
        var s = Area.Sections("Воздуховод 200x100 и ⌀160");
        Assert.Equal(2, s.Count);
        Assert.Equal(("rect", 200d, 100d), s[0]);
        Assert.Equal(("round", 160d, 160d), s[1]);
    }

    [Fact]
    public void Sections_RectSkipsRoundInside()
    {
        // round match inside rect span is skipped
        var s = Area.Sections("200x100");
        Assert.Single(s);
        Assert.Equal("rect", s[0].Kind);
    }

    [Fact]
    public void Perimeter_RectAndRound()
    {
        Assert.Equal(0.6, Area.Perimeter(("rect", 200d, 100d)), 6);
        Assert.Equal(Math.PI * 0.16, Area.Perimeter(("round", 160d, 160d)), 6);
    }

    [Theory]
    [InlineData("Воздуховод 200x100", "м", true)]
    [InlineData("Установка", "шт", false)]
    public void IsDuctFamily_Works(string name, string unit, bool expected) =>
        Assert.Equal(expected, Area.IsDuctFamily(name));

    [Fact]
    public void AutoArea_AlreadyM2()
    {
        var (v, how) = Area.AutoArea("Что-то", "м²");
        Assert.Equal(1.0, v);
        Assert.Equal("уже в м²", how);
    }

    [Fact]
    public void AutoArea_DuctMeter()
    {
        var (v, _) = Area.AutoArea("Воздуховод 200x100", "м");
        Assert.NotNull(v);
        Assert.Equal(0.6, v!.Value, 6);
    }

    [Fact]
    public void AutoArea_FittingBend()
    {
        var (v, how) = Area.AutoArea("Отвод 90° 200x100", "шт");
        Assert.NotNull(v);
        Assert.Contains("отвод", how);
    }

    [Fact]
    public void AutoArea_NoSection_ReturnsNull()
    {
        var (v, _) = Area.AutoArea("Воздуховод", "м");
        Assert.Null(v);
    }

    [Fact]
    public void AreaPerUnit_ManualOverrides()
    {
        var (v, how) = Area.AreaPerUnit("Воздуховод 200x100", "м", 0.5);
        Assert.Equal(0.5, v);
        Assert.Equal("вписано вручную", how);
    }

    [Fact]
    public void RowArea_MultipliesByQty()
    {
        Assert.Equal(3.0, Area.RowArea("Воздуховод 200x100", "м", 5, null)!.Value, 6);
    }

    [Fact]
    public void RowArea_NullWhenNoArea()
    {
        Assert.Null(Area.RowArea("Установка", "шт", 5, null));
    }

    [Fact]
    public void SteelThickness_Explicit()
    {
        Assert.Equal(0.8, Area.SteelThickness("Воздуховод b=0.8 200x100"), 6);
    }

    [Fact]
    public void SteelThickness_BySize()
    {
        Assert.Equal(0.5, Area.SteelThickness("Воздуховод 200x100"), 6);
    }

    [Fact]
    public void Material_DuctReturnsSteel()
    {
        var m = Area.Material("Воздуховод 200x100", "м");
        Assert.NotNull(m);
        Assert.StartsWith("steel:", m!.Value.Key);
    }

    [Fact]
    public void Material_Fire()
    {
        var m = Area.Material("Огнезащита воздуховодов EI60", "м");
        Assert.NotNull(m);
        Assert.StartsWith("fire:", m!.Value.Key);
    }

    [Fact]
    public void Material_Insulation()
    {
        var m = Area.Material("Теплоизоляция 50мм", "м");
        Assert.NotNull(m);
        Assert.StartsWith("insul:", m!.Value.Key);
    }

    [Fact]
    public void Material_NonDuctReturnsNull()
    {
        Assert.Null(Area.Material("Установка", "шт"));
    }

    [Fact]
    public void IsSummary_JsonWithM2()
    {
        Assert.True(Area.IsSummary("{\"m2\":true}"));
        Assert.False(Area.IsSummary("{\"x\":1}"));
        Assert.False(Area.IsSummary(null));
        Assert.False(Area.IsSummary("not json"));
    }
}