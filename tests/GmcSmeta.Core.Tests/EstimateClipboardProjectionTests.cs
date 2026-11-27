using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class EstimateClipboardProjectionTests
{
    private static EstimateRow Row(int id, int section, bool system = false, int? parent = null) =>
        new(system ? RowKind.System : RowKind.Position, system ? 0 : parent.HasValue ? 2 : 1,
            id, id, parent.HasValue, id.ToString(), $"Строка {id}", "шт", 2m, 17m, 34m,
            11m, false, "Примечание") { SystemId = section, ParentPositionId = parent,
            WorkPrice = 4m, Formulas = "{\"qty\":\"=1+1\"}" };

    [Fact]
    public void MixedSelectionPreservesOrderAndCopiesChildrenOnce()
    {
        var rows = new[] { Row(1, 10, true), Row(2, 10), Row(3, 10, parent: 2),
            Row(4, 10), Row(5, 20, true), Row(6, 20), Row(7, 20, parent: 6) };
        var chosen = new HashSet<int> { 1, 2, 3, 7 };
        var copied = EstimateClipboardProjection.Build(rows, r => r, r => chosen.Contains(r.Id));
        Assert.Equal(new[] { "Строка 2", "Строка 4", "Строка 7" }, copied.Select(r => r.Name));
        Assert.Equal("Строка 3", Assert.Single(copied[0].Children!).Name);
        Assert.Equal(17m, copied[0].Price);
        Assert.Equal(11m, copied[0].PurchasePrice);
        Assert.Equal(4m, copied[0].WorkPrice);
        Assert.Equal(rows[1].Formulas, copied[0].Formulas);
        Assert.Equal(0m, copied[2].Price);
        Assert.Null(copied[2].WorkPrice);
        Assert.Null(copied[2].Children);
        Assert.Equal(rows[6].Formulas, copied[2].Formulas);
    }

    [Fact]
    public void LargeSectionSelectionPreservesAll25000RowsInLinearProjection()
    {
        var rows = new List<EstimateRow>();
        var next = 1;
        for (var section = 1; section <= 100; section++)
        {
            rows.Add(Row(next++, section, true));
            for (var n = 0; n < 125; n++)
            {
                var id = next++;
                rows.Add(Row(id, section));
                rows.Add(Row(next++, section, parent: id));
            }
        }
        var visits = 0;
        var copied = EstimateClipboardProjection.Build(rows, r => { visits++; return r; }, r => r.IsSystem);
        Assert.Equal(12500, copied.Count);
        Assert.Equal(25000, copied.Sum(r => 1 + r.Children!.Count));
        Assert.Equal(rows.Where(r => !r.IsSystem && !r.IsSub).Select(r => r.Name), copied.Select(r => r.Name));
        Assert.All(copied, r => Assert.Single(r.Children!));
        Assert.True(visits <= rows.Count * 6);
    }

    [Fact]
    public void NoSelectedRowsProducesNoCopy()
    {
        var rows = new[] { Row(1, 10, true), Row(2, 10), Row(3, 10, parent: 2) };
        Assert.Empty(EstimateClipboardProjection.Build(rows, r => r, _ => false));
    }
}
