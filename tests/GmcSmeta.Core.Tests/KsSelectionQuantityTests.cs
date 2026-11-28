using System.Globalization;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsSelectionQuantityTests
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    [Fact]
    public void EmptySelectionDoesNotDisplaySummary()
    {
        Assert.Empty(KsSelectionQuantity.Describe(Array.Empty<(string, decimal)>(), Ru));
    }

    [Fact]
    public void EquivalentUnitsAreCombinedInFirstSeenOrder()
    {
        var rows = new[] { (" м² ", 1.25m), ("М2.", 2.5m), ("ШТ.", 3m),
            ("шт", 4m), ("м³", 1m), ("м3", 2m) };
        Assert.Equal("Выделено: 6  ·  Объём в акте: 3,75 м²  ·  7 шт.  ·  3 м³",
            KsSelectionQuantity.Describe(rows, Ru));
    }

    [Fact]
    public void RoundingOccursAfterSumAndRetainsMidpointToEven()
    {
        var rows = new[] { ("м", 1.0003m), ("м", 0.0002m), ("кг", 1.0015m) };
        Assert.Equal("Выделено: 3  ·  Объём в акте: 1 м  ·  1,002 кг",
            KsSelectionQuantity.Describe(rows, Ru));
    }

    [Fact]
    public void UnknownUnitKeepsFirstSpellingAndBlankUnitAddsNoTrailingSpace()
    {
        var rows = new[] { (" Компл. ", 1m), ("компл", 2m), ("", 4m) };
        Assert.Equal("Выделено: 3  ·  Объём в акте: 3 Компл.  ·  4",
            KsSelectionQuantity.Describe(rows, Ru));
    }

    [Fact]
    public void CultureAndNegativeOrZeroQuantitiesArePreserved()
    {
        var rows = new[] { ("м", -1.25m), ("шт", 0m) };
        Assert.Equal("Выделено: 2  ·  Объём в акте: -1.25 м  ·  0 шт.",
            KsSelectionQuantity.Describe(rows, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EditorUsesIndependentSummaryAndStillExcludesSectionHeaders()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs")))
            root = root.Parent;
        Assert.NotNull(root);
        var source = File.ReadAllText(Path.Combine(root!.FullName, "src", "GmcSmeta.App", "ActsEditorView.xaml.cs"));
        Assert.Contains("Where(r => !r.IsSection).ToList()", source);
        Assert.Contains("KsSelectionQuantity.Describe(selected.Select(r => (r.Unit, r.NowValue))", source);
        Assert.Contains("SelectionQty.Visibility = Visibility.Collapsed;", source);
        Assert.Contains("SelectionQty.ToolTip = text;", source);
    }
}
