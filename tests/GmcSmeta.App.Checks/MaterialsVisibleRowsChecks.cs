using GmcSmeta.App;
using Row = GmcSmeta.App.EstimateView.MaterialsRow;

internal static class MaterialsVisibleRowsChecks
{
    internal static void Run()
    {
        var rows = new List<Row>();
        for (int system = 1; system <= 100; system++)
        {
            var header = new Row { IsSystemHeader = true, Name = "Система " + system, SystemId = system };
            rows.Add(header);
            for (int i = 0; i < 250; i++)
                rows.Add(new Row { Parent = header, Name = i % 2 == 0 ? "Труба сталь" : "Клапан", SystemId = system,
                    SystemLabel = "Система " + system, PositionId = system * 1000 + i,
                    NoCostFlag = i % 3 == 0, NoPriceFlag = i % 5 == 0 });
        }
        var supplier = new Row { IsGroup = true, IsSystemHeader = true, Name = "Поставщик: Север" };
        rows.Add(supplier);
        rows.Add(new Row { Parent = supplier, Name = "Труба медь", SystemLabel = "ПВ-1", SystemId = 999 });
        rows.Add(new Row { IsGroup = true, Name = "Пустая группа" });
        var index = new MaterialsVisibleRows(rows);
        foreach (var query in new[] { "", "труба сталь", "система 1", "труба система", "север", "пустая" })
        foreach (var missing in new[] { false, true })
        foreach (var noPrice in new[] { false, true })
        foreach (long? system in new long?[] { null, 1, 999 })
        {
            var expected = Legacy(rows, query, missing, noPrice, system);
            var actual = index.Filter(query, missing, noPrice, system);
            if (!MaterialsVisibleRows.SameReferences(expected, actual))
                throw new Exception("Materials filter changed visibility/order for " + query);
        }
        var original = index.Filter("", false, false, null);
        var same = index.Filter("  ", false, false, null);
        if (!MaterialsVisibleRows.SameReferences(original, same))
            throw new Exception("No-op filter changed references.");
        var replacement = original.ToList();
        replacement[1] = new Row { Name = original[1].Name };
        if (MaterialsVisibleRows.SameReferences(original, replacement))
            throw new Exception("Authoritative row replacement was skipped.");
        CheckNested();
        Console.WriteLine("Materials 25,000 positions: filter parity, nested disclosure and reference checks OK.");
    }

    private static void CheckNested()
    {
        var supplier = new Row { IsSystemHeader = true, IsGroup = true, Name = "Север" };
        var group = new Row { Parent = supplier, IsGroup = true, Name = "Труба", IsExpanded = false, SystemId = 1 };
        var first = new Row { Parent = group, Name = "Труба", SystemId = 1 };
        var second = new Row { Parent = group, Name = "Труба", SystemId = 2, NoCostFlag = true };
        var section = new Row { IsSystemHeader = true, SystemId = 3 };
        var area = new Row { Parent = section, Name = "м²", SystemId = 3, IsExpanded = false };
        var child = new Row { Parent = area, Name = "Комплект", IsSub = true, SystemId = 3 };
        var rows = new[] { supplier, group, first, second, section, area, child };
        var index = new MaterialsVisibleRows(rows);
        void Equal(IReadOnlyList<Row> actual, params Row[] expected)
        {
            if (!actual.SequenceEqual(expected)) throw new Exception("Nested material projection changed order or membership.");
        }
        Equal(index.Filter("", false, false, null), supplier, group, section, area);
        Equal(index.Filter("", true, false, 2), supplier, group);
        index.SetNestedExpanded(true);
        Equal(index.Filter("", true, false, 2), supplier, group, second);
        Equal(index.Filter("", false, false, null), rows);
        Equal(index.Filter("", false, false, null, export: true), supplier, group, section, area, child);
        index.SetNestedExpanded(false);
        Equal(index.Filter("", false, false, null), supplier, group, section, area);
        Equal(index.Filter("", false, false, null, export: true), supplier, group, section, area, child);
    }

    // Previous two-pass implementation as an independent oracle for non-nested rows.
    private static List<Row> Legacy(List<Row> rows, string query, bool missing, bool noPrice, long? system)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant()).ToArray();
        bool Match(string text) => words.All(w => text.ToLowerInvariant().Contains(w));
        bool PositionOk(Row r) => (Match(r.Name) || Match(r.SystemLabel)) &&
            (!missing || r.NoCostFlag) && (!noPrice || r.NoPriceFlag) &&
            (system is null || r.SystemId == system);
        var counts = new Dictionary<Row, int>();
        Row? header = null;
        foreach (var row in rows)
        {
            if (row.IsSystemHeader || row.IsGroup) { header = row; counts[row] = 0; }
            else if (PositionOk(row) && header is not null) counts[header]++;
        }
        return rows.Where(r => r.IsGroup
            ? counts.GetValueOrDefault(r) > 0 || (!r.IsSystemHeader && !missing && !noPrice && system is null && Match(r.Name))
            : r.IsSystemHeader
                ? counts.GetValueOrDefault(r) > 0 && (system is null || r.SystemId == system)
                : PositionOk(r)).ToList();
    }
}
