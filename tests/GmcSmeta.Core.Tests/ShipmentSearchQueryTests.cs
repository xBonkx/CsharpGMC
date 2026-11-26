using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class ShipmentSearchQueryTests
{
    [Fact]
    public void ReusedQueryMatchesOriginalAcrossLargeInputAndQueryChanges()
    {
        var search = new ShipmentSearchQuery();
        foreach (var text in new string?[] { null, "", "  ", "ПВ-1 труба", "МЕДЬ", "труба\tмедь",
            "труба  медь", "система 100", "нет", "ПВ-1 труба", "" })
        {
            var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < 25000; i++)
            {
                var name = i % 3 == 0 ? "Труба\tмедь" : i % 2 == 0 ? "Труба медь" : "Клапан";
                var system = i % 5 == 0 ? "ПВ-1" : "Система " + i % 100;
                var expected = words.All(w => (name + " " + system).Contains(w, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(expected, search.Matches(text, name, system));
            }
        }
    }

    [Fact]
    public void FieldsAndCurrentQueryAreNotCachedAsOldRowValues()
    {
        var search = new ShipmentSearchQuery();
        Assert.True(search.Matches("пв труба", "Труба", "ПВ"));
        Assert.False(search.Matches("пв труба", "Клапан", "ПВ"));
        Assert.True(search.Matches("клапан", "Клапан", "ПВ"));
        Assert.False(search.Matches("труба", "Клапан", "ПВ"));
        Assert.True(search.Matches("", "", ""));
    }
}
