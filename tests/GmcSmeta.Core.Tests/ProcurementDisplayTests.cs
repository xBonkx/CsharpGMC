using System.Globalization;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public class ProcurementDisplayTests
{
    [Theory]
    [InlineData("1,5", 15)]
    [InlineData("1.5", 15)]
    [InlineData("1 234,5", 12345)]
    public void DecimalInputIsIndependentOfSystemLocale(string input, int tenths)
    {
        Assert.True(ProcurementDisplay.TryParseNumber(input, out var value));
        Assert.Equal(tenths / 10m, value);
    }

    [Fact]
    public void ExpectedDateAndRussianQuantityMatchPython()
    {
        var now = new DateTime(2026, 11, 24);
        Assert.Equal("1,5", ProcurementDisplay.Quantity(1.5m));
        Assert.Equal("25.11", ProcurementDisplay.ExpectedCaption(now.AddDays(1), now));
        Assert.Equal("01.12.27", ProcurementDisplay.ExpectedCaption(new DateTime(2027, 12, 1), now));
        Assert.Equal("late", ProcurementDisplay.ExpectedState(now.AddDays(-1), "ordered", now));
        Assert.Equal("soon", ProcurementDisplay.ExpectedState(now.AddDays(2), "ordered", now));
        Assert.Equal("done", ProcurementDisplay.ExpectedState(now.AddDays(-1), "issued", now));
    }

    [Theory]
    [InlineData(10, 2, "part_issued", "8")]
    [InlineData(10, 11, "issued", "")]
    [InlineData(10, 0, "skip", "")]
    [InlineData(0, 0, "zero", "")]
    public void RemainingMatchesPython(int planned, int issued, string status, string expected) =>
        Assert.Equal(expected, ProcurementDisplay.Remaining(planned, issued, status, CultureInfo.InvariantCulture));

    [Fact]
    public void CombinedColumnsRetainBothValues()
    {
        Assert.Equal("З5 / сч. С7 · опл. 24.11", ProcurementDisplay.OrderCaption("З5", "С7", new DateTime(2026, 11, 24)));
        Assert.Equal("замена: ф315; срочно", ProcurementDisplay.NoteCaption("срочно", "ф315"));
        Assert.Equal("срочно", ProcurementDisplay.NoteCaption("срочно", ""));
        Assert.Equal("", ProcurementDisplay.OrderCaption("", "", null));
    }

    [Theory]
    [InlineData("+7", 2026, 12, 1)]
    [InlineData("25.11", 2026, 11, 25)]
    [InlineData("01.12.2027", 2027, 12, 1)]
    public void DueDateAcceptsPythonFormats(string text, int y, int m, int day)
    {
        Assert.True(ProcurementDisplay.TryParseDue(text, new DateTime(2026, 11, 24), out var value));
        Assert.Equal(new DateTime(y, m, day), value);
    }

    [Fact]
    public void InvalidDueDoesNotSilentlyClear()
    {
        Assert.False(ProcurementDisplay.TryParseDue("31.02", new DateTime(2026, 11, 24), out _));
        Assert.True(ProcurementDisplay.TryParseDue("", new DateTime(2026, 11, 24), out var date));
        Assert.Null(date);
    }
}
