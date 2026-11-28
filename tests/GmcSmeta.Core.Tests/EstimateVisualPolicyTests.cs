using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateVisualPolicyTests
{
    [Theory]
    [InlineData("КП", null, "ГМЦ", "Полное название", "КП", "ГМЦ")]
    [InlineData("Договор", "Приложение № 3", "ГМЦ", "Приложение № 3", "Приложение", "ГМЦ")]
    [InlineData("Допсоглашение", "", null, "Полное название", "Допсоглашение", "организация не указана")]
    [InlineData("КП", "   ", "", "Полное название", "КП", "организация не указана")]
    public void HeaderKeepsLegalTitleAndMetadata(string kind, string? annex, string? company,
        string title, string shownKind, string shownCompany)
    {
        var header = EstimateVisualPolicy.HeaderText("Полное название", annex, "Объект", kind, company);
        Assert.Equal(title, header.Title);
        Assert.Equal("Полное название", header.FullTitle);
        Assert.Equal("Объект", header.Object);
        Assert.Equal(shownKind, header.Kind);
        Assert.Equal(shownCompany, header.Company);
    }

    [Theory]
    [InlineData(false, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void AddVisibilityDoesNotGrantWriteRights(bool locked, bool rest, bool rights, bool expected) =>
        Assert.Equal(expected, EstimateVisualPolicy.ShowAddActions(locked, rest, rights));

    [Theory]
    [InlineData(null, true, true, false, true)]
    [InlineData(null, false, true, true, false)]
    [InlineData(null, false, false, true, true)]
    [InlineData(null, true, false, false, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, false, true, false, true)]
    public void PurchaseChoiceWinsOverDataAndRefresh(bool? chosen, bool hasPurchase,
        bool first, bool current, bool expected) =>
        Assert.Equal(expected, EstimateVisualPolicy.ShowPurchase(chosen, hasPurchase, first, current));

    [Theory]
    [InlineData("pos", true)]
    [InlineData("sum", false)]
    [InlineData("percent", false)]
    [InlineData("systems", false)]
    [InlineData("", false)]
    public void WorkColumnsFollowModeOnly(string mode, bool expected) =>
        Assert.Equal(expected, EstimateVisualPolicy.ShowWorkColumns(mode));

    [Theory]
    [InlineData("1234567.89", "1 234 567,89", "1234567,89")]
    [InlineData("-1234.5", "-1 234,50", "-1234,5")]
    [InlineData("0", "0,00", "0")]
    [InlineData("1.126", "1,13", "1,126")]
    public void RussianMoneyAndQuantityUseExplicitSeparators(string text, string money, string qty)
    {
        var value = decimal.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(money, EstimateVisualPolicy.Money(value));
        Assert.Equal(qty, EstimateVisualPolicy.Quantity(value));
        Assert.Equal("", EstimateVisualPolicy.Money(null));
    }

    [Theory]
    [InlineData(.9, .8)]
    [InlineData(1, .9)]
    [InlineData(1, 1)]
    [InlineData(1.1, 1.2)]
    [InlineData(1.2, 1.8)]
    public void TableZoomAndAppScaleAreIndependent(double scale, double zoom)
    {
        const double dip = 10 * 96d / 72;
        var font = EstimateVisualPolicy.TableFont(dip, .95, zoom);
        Assert.Equal(dip * zoom * scale, font.Body * scale, 8);
        Assert.Equal(dip * .95 * zoom * scale, font.Header * scale, 8);
        Assert.Equal(.95, font.Header / font.Body, 8);
    }

    [Fact]
    public void InvalidZoomIsNormalized()
    {
        Assert.Equal(1, EstimateVisualPolicy.NormalizeZoom(double.NaN));
        Assert.Equal(.8, EstimateVisualPolicy.NormalizeZoom(.1));
        Assert.Equal(1.8, EstimateVisualPolicy.NormalizeZoom(4));
    }
}
