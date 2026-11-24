using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentQuantityPolicyTests
{
    [Theory]
    [InlineData("1,123456", "ru-RU")]
    [InlineData("1.123456", "en-US")]
    [InlineData("0,000002", "ru-RU")]
    public void EditFormatting_RoundTripsWithoutChangingLedgerQuantity(string input, string cultureName)
    {
        Assert.True(ShipmentQuantityPolicy.TryParseEdit(input, out var value));
        var shown = ShipmentQuantityPolicy.Format(value, System.Globalization.CultureInfo.GetCultureInfo(cultureName));
        Assert.True(ShipmentQuantityPolicy.TryParseEdit(shown, out var parsed));
        Assert.Equal(value, parsed);
    }

    [Theory]
    [InlineData("0.000001")]
    [InlineData("0.0000001")]
    [InlineData("-0.000001")]
    [InlineData("1.1234567")]
    [InlineData("1000000001")]
    [InlineData("1e3")]
    public void EditRejectsValuesThatWouldRoundOrEraseStock(string input) =>
        Assert.False(ShipmentQuantityPolicy.TryParseEdit(input, out _));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    public void ExplicitZeroRemovesLine(string input)
    {
        Assert.True(ShipmentQuantityPolicy.TryParseEdit(input, out var value));
        Assert.Equal(0m, value);
    }

    [Fact]
    public void RemainingConfirmation_UsesTheSameToleranceAsSql()
    {
        Assert.False(ShipmentQuantityPolicy.ExceedsRemaining(2m, 2.000001m));
        Assert.True(ShipmentQuantityPolicy.ExceedsRemaining(2m, 2.000002m));
        Assert.True(ShipmentQuantityPolicy.ExceedsRemaining(0m, 1m));
    }

    [Theory]
    [InlineData(0, 0, 3, false)]
    [InlineData(5, 2, 3, false)]
    [InlineData(5, 2, 4, true)]
    [InlineData(5, 7, 1, true)]
    public void StockWarning_MatchesPythonReceiptRule(decimal received, decimal issued,
        decimal requested, bool expected) =>
        Assert.Equal(expected, ShipmentQuantityPolicy.ExceedsStock(received, issued, requested));

    [Theory]
    [InlineData(10, 4, 6)]
    [InlineData(10, 11, 0)]
    [InlineData(10, 0, 10)]
    public void Remaining_ExcludesAlreadyIssued(decimal estimate, decimal issued, decimal expected) =>
        Assert.Equal(expected, ShipmentQuantityPolicy.Remaining(estimate, issued));

    [Fact]
    public void ExistingFixedShipment_CanCorrectItsOwnPreviouslyIssuedQuantity()
    {
        Assert.Equal(6m, ShipmentQuantityPolicy.AvailableForChange(10m, 7m, 3m));
        Assert.Equal(3m, ShipmentQuantityPolicy.AvailableForChange(10m, 7m, 0m));
    }

    [Theory]
    [InlineData(10, 4, 8, 4)]
    [InlineData(10, 4, 0, 6)]
    [InlineData(10, 4, 20, 6)]
    public void SuggestedQuantity_NeverExceedsUnissuedEstimate(
        decimal estimate, decimal issued, decimal received, decimal expected) =>
        Assert.Equal(expected, ShipmentQuantityPolicy.SuggestedFromStock(estimate, issued, received));
}
