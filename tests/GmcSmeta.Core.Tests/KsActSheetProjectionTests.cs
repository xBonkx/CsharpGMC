using System.Globalization;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsActSheetProjectionTests
{
    private static SqlServerActs.ActSheetRow Row(long id, long system, string number,
        bool outside = false, bool inAct = true) => new(id, system, number, "Position", "pcs",
        5m, 10m, 2m, outside, "", inAct ? id : null, 2m, "", 10m, 2m, 1m, 3m, true);

    [Theory]
    [InlineData(false, "1", "1.1", "3", "3.1")]
    [InlineData(true, " 7 ", "7.1", "8,2", "8,2.1")]
    public void NumberingPreservesAnchorsAndOriginalRowOrder(bool manual, string first,
        string extraFirst, string second, string extraSecond)
    {
        var systems = new (long Id, string Name, long Parent)[]
        {
            (1, "Без системы", 0), (2, "Раздел", 0), (3, "Подраздел", 2),
        };
        var rows = new[] { Row(4, 2, "x", true), Row(1, 1, " 7 "), Row(2, 1, "", true),
            Row(3, 2, "8,2"), Row(5, 3, "", true), Row(6, 3, "", true, false) };
        // Group order for numbering is independent from final presentation order.
        var result = KsActSheetProjection.NumberRows(systems,
            new[] { rows[0], rows[1], rows[2], rows[3], rows[4], rows[5] }, manual);
        // The extra in system 2 precedes the second normal row and uses the
        // previous system's anchor. A subsection still counts in KP numbering.
        Assert.Equal(new long[] { 4, 1, 2, 3, 5, 6 }, result.Rows.Select(p => p.PositionId));
        Assert.Equal(first, result.Rows[1].NumText);
        Assert.Equal(extraFirst, result.Rows[2].NumText);
        Assert.Equal(manual ? "7.2" : "1.2", result.Rows[0].NumText);
        Assert.Equal(second, result.Rows[3].NumText);
        Assert.Equal(extraSecond, result.Rows[4].NumText);
        Assert.Equal("", result.Rows[5].NumText);
        Assert.Equal(manual ? "7" : "1", result.RefDisplay[1]);
        Assert.Equal(manual ? "8.2" : "3", result.RefDisplay[3]);
        Assert.Equal(2, result.RefNumbers.Count);
        Assert.Equal(1m, result.Rows[1].ClosedByOthers);
        Assert.Equal(3m, result.Rows[1].Issued);
        Assert.True(result.Rows[1].TrackIssues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IndexedProjectionMatchesPreviousScanFor25000Positions(bool manual)
    {
        var systems = Enumerable.Range(1, 500)
            .Select(i => ((long)i, i == 1 ? "Без системы" : "System " + i, 0L)).ToArray();
        var rows = Enumerable.Range(1, 25000).Select(i =>
            Row(i, (i - 1) % 500 + 1, i % 7 == 0 ? "" : i % 11 == 0 ? " 7,1 " : i.ToString(CultureInfo.InvariantCulture),
                i % 13 == 0, i % 17 != 0)).ToArray();
        // Independent scan reproduces the old per-system loops.
        var kp = new Dictionary<long, int>();
        var next = 1;
        foreach (var sy in systems)
        {
            if (sy.Item2 != "Без системы") next++;
            foreach (var p in rows.Where(p => p.SystemId == sy.Item1 && !p.OutOfKp))
                kp[p.PositionId] = next++;
        }
        var extra = new Dictionary<long, string>();
        var anchor = "";
        var suffix = 0;
        foreach (var sy in systems)
            foreach (var p in rows.Where(p => p.SystemId == sy.Item1))
                if (!p.OutOfKp)
                {
                    anchor = manual && p.NumText.Trim().Length != 0 ? p.NumText.Trim() :
                        kp[p.PositionId].ToString(CultureInfo.InvariantCulture);
                    suffix = 0;
                }
                else if (p.LineId is not null) extra[p.PositionId] = $"{anchor}.{++suffix}";
        var expected = rows.Select(p => p with { NumText = p.OutOfKp
            ? extra.GetValueOrDefault(p.PositionId, "")
            : manual ? p.NumText : kp[p.PositionId].ToString(CultureInfo.InvariantCulture) }).ToArray();
        var result = KsActSheetProjection.NumberRows(systems, rows, manual);
        Assert.Equal(expected, result.Rows.ToArray());
        var refs = rows.Where(p => !p.OutOfKp).ToDictionary(p => p.PositionId, p =>
            (manual && p.NumText.Trim().Length != 0 ? p.NumText.Trim() :
                kp[p.PositionId].ToString(CultureInfo.InvariantCulture)).Replace(',', '.').Trim());
        Assert.Equal(refs.OrderBy(p => p.Key), result.RefNumbers.OrderBy(p => p.Key));
        var display = refs.GroupBy(p => p.Value).Where(g => g.Count() == 1)
            .Select(g => g.Single()).OrderBy(p => p.Key);
        Assert.Equal(display, result.RefDisplay.OrderBy(p => p.Key));
    }

    [Fact]
    public void EmptyProjectionIsValid()
    {
        var result = KsActSheetProjection.NumberRows(
            Array.Empty<(long, string, long)>(), Array.Empty<SqlServerActs.ActSheetRow>(), false);
        Assert.Empty(result.Rows);
        Assert.Empty(result.RefNumbers);
        Assert.Empty(result.RefDisplay);
    }

    private static SqlServerActs.ActSheetData Sheet(params SqlServerActs.ActSheetRow[] rows) =>
        new(new[] { new SqlServerActs.ActSheetSystem(1, "П1", false, 0),
            new SqlServerActs.ActSheetSystem(2, "В1", true, 1),
            new SqlServerActs.ActSheetSystem(3, "Пустая", false, 0) },
            rows, new Dictionary<long, string>(), new Dictionary<long, string>(), false, "r4");

    [Theory]
    [InlineData(false, 0.02)]
    [InlineData(true, 0.03)]
    public void SectionsIncludeManualAmountsAndNegativeLines(bool roundRows, double expected)
    {
        var sheet = Sheet(Row(1, 1, "1") with { LineQty = 1m, LinePrice = 0.025m },
            Row(2, 2, "2") with { LineQty = -2m, LinePrice = 10m },
            Row(3, 1, "3", inAct: false));
        var manual = new KsActAllocation.SectionAmounts(
            new Dictionary<long, decimal> { [1] = 5, [3] = 7, [0] = 2 },
            new Dictionary<long, decimal> { [1] = 10, [2] = 1, [0] = 3 });
        var sections = KsActSheetProjection.Sections(sheet, manual, false, roundRows);
        Assert.Equal(new long[] { 1, 2, 3, 0 }, sections.Select(s => s.SystemId));
        Assert.Equal((decimal)expected + 15m, sections[0].Amount);
        Assert.Equal(-19m, sections[1].Amount);
        Assert.Equal(7m, sections[2].Amount);
        Assert.Equal(5m, sections[3].Amount);
        Assert.True(sections[3].Unallocated);
        Assert.True(sections[0].InAct);
        var perPosition = KsActSheetProjection.Sections(sheet, manual, true, roundRows);
        Assert.Equal(new long[] { 1, 2, 0 }, perPosition.Select(s => s.SystemId));
        Assert.Equal((decimal)expected + 10m, perPosition[0].Amount);
    }

    [Fact]
    public void SectionProjectionSupports25000PositionsWithoutDroppingEmptyOrUnclosedRules()
    {
        var sheet = Sheet(Enumerable.Range(1, 25000).Select(i =>
            Row(i, i % 2 + 1, i.ToString(), inAct: i % 3 != 0)).ToArray());
        var sections = KsActSheetProjection.Sections(sheet, null, false, false);
        Assert.Equal(2, sections.Count);
        foreach (var section in sections)
            Assert.Equal(sheet.Positions.Where(r => r.SystemId == section.SystemId && r.LineId is not null)
                .Sum(r => decimal.Round(r.LineQty * r.LinePrice, 2)), section.Amount);
    }

    [Fact]
    public void ReadbackLayoutAllowsQuantityChangesButRejectsRenumberingOrMembershipChanges()
    {
        var original = Sheet(Row(1, 1, "1"), Row(2, 2, "2"));
        Assert.True(KsActSheetProjection.SameRowLayout(original,
            original with { Positions = original.Positions.Select(r => r with { LineQty = 7m }).ToArray() }));
        Assert.False(KsActSheetProjection.SameRowLayout(original,
            original with { Positions = original.Positions.Take(1).ToArray() }));
        Assert.False(KsActSheetProjection.SameRowLayout(original,
            original with { Positions = original.Positions.Reverse().ToArray() }));
        Assert.False(KsActSheetProjection.SameRowLayout(original,
            original with { Positions = original.Positions.Select(r => r with { NumText = "9" }).ToArray() }));
    }


    [Fact]
    public void QuantityPatchFormatsOnlyChangedRowsAndAlwaysRestoresEditedCell()
    {
        var original = Sheet(Enumerable.Range(1, 25000)
            .Select(i => Row(i, i % 2 + 1, i.ToString())).ToArray());
        var next = original with { Positions = original.Positions.Select(r =>
            r.PositionId == 500 ? r with { LineQty = 3m } :
            r.PositionId == 1500 ? r with { LineFormula = "@500*2", LineQty = 6m } : r with { }).ToArray() };
        var patch = Assert.IsAssignableFrom<IReadOnlyDictionary<long, SqlServerActs.ActSheetRow>>(
            KsActSheetProjection.QuantityPatch(original, next, 200));
        Assert.Equal(new long[] { 200, 500, 1500 }, patch.Keys.OrderBy(id => id));
        Assert.Equal(6m, patch[1500].LineQty);
        Assert.Equal("@500*2", patch[1500].LineFormula);
        var noop = Assert.IsAssignableFrom<IReadOnlyDictionary<long, SqlServerActs.ActSheetRow>>(
            KsActSheetProjection.QuantityPatch(next, next, 200));
        Assert.Equal(200L, Assert.Single(noop).Key);
    }

    [Fact]
    public void QuantityPatchReformatsUnchangedFormulasWhenReferenceMapChanges()
    {
        var original = Sheet(Row(1, 1, "1"), Row(2, 1, "2") with { LineFormula = "@1" },
            Row(3, 1, "3")) with { RefDisplay = new Dictionary<long, string> { [1] = "1" } };
        var sameMap = original with { RefDisplay = new Dictionary<long, string> { [1] = "1" } };
        Assert.Single(KsActSheetProjection.QuantityPatch(original, sameMap, 3)!);
        var newMap = original with { RefDisplay = new Dictionary<long, string>() };
        var patch = KsActSheetProjection.QuantityPatch(original, newMap, 3)!;
        Assert.Equal(new long[] { 2, 3 }, patch.Keys.OrderBy(id => id));
    }

    [Fact]
    public void QuantityPatchRejectsStructuralChangesBeforePublishingAnyDelta()
    {
        var original = Sheet(Row(1, 1, "1"), Row(2, 2, "2"));
        Assert.Null(KsActSheetProjection.QuantityPatch(null, original, 1));
        Assert.Null(KsActSheetProjection.QuantityPatch(original,
            original with { Positions = original.Positions.Reverse().ToArray() }, 1));
        Assert.Null(KsActSheetProjection.QuantityPatch(original,
            original with { Positions = new[] { original.Positions[0] } }, 1));
        Assert.Null(KsActSheetProjection.QuantityPatch(original,
            original with { Form = "dns" }, 1));
    }

}
