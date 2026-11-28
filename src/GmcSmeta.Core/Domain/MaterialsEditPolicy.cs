using System.Globalization;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

public readonly record struct MaterialsSelectionRow(bool IsSystemHeader, bool IsGroup,
    bool IsSub, bool IsWork, long SystemId, long PositionId, IReadOnlyList<long> MemberIds);

/// <summary>Pure edit planning. The view and SQL writers own context, revision, lock and transaction checks.</summary>
public static class MaterialsEditPolicy
{
    public static List<long> SelectedIds(IEnumerable<CostMaterialsPosition> top,
        IEnumerable<MaterialsSelectionRow> selection)
    {
        var bySystem = top.ToLookup(p => p.SystemId, p => p.Id);
        var result = new List<long>();
        var seen = new HashSet<long>();
        void Add(long id) { if (id > 0 && seen.Add(id)) result.Add(id); }
        foreach (var row in selection)
        {
            if (row.IsSystemHeader && !row.IsGroup)
                foreach (var id in bySystem[row.SystemId]) Add(id);
            else if (row.MemberIds.Count > 0)
                foreach (var id in row.MemberIds) Add(id);
            else if (row.PositionId > 0 && !row.IsSub)
                Add(row.PositionId);
        }
        return result;
    }

    public static long[] ClearIds(string field, IEnumerable<MaterialsSelectionRow> selection) => selection
        .Where(row => !row.IsSystemHeader)
        .Where(row => field != "price" || !row.IsSub && !row.IsWork)
        .SelectMany(row => row.IsGroup ? row.MemberIds : new[] { row.PositionId })
        .Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();

    public readonly record struct Validation(bool Handled, bool Allowed, string? Error);

    public static Validation Validate(string column, string text)
    {
        var trimmed = text.Trim();
        switch (column)
        {
            case "Наименование":
                return trimmed.Length == 0 ? new(true, false, "Наименование не может быть пустым") : new(true, true, null);
            case "Цена клиенту":
            case "Закуп за ед.":
            case "Факт по счёту":
                return trimmed.Length > 0 && NumberText.Parse(text) is null
                    ? new(true, false, "Недопустимое число: " + trimmed) : new(true, true, null);
            case "Наценка, %":
                if (trimmed.Length == 0) return new(true, false, null);
                var pct = NumberText.Parse(text);
                if (pct is null) return new(true, false, "Недопустимое число: " + trimmed);
                if (pct < -100m) return new(true, false, "Наценка не может быть меньше −100 %: цена ушла бы в минус");
                return new(true, true, null);
            case "Поставщик":
            case "Пометки":
                return new(true, true, null);
            default:
                return new(false, false, null);
        }
    }

    public static List<(long PositionId, string Input)> MarkupEdits(IEnumerable<long> ids,
        IReadOnlyDictionary<long, CostMaterialsPosition> byId, decimal percent)
    {
        var edits = new List<(long PositionId, string Input)>();
        foreach (var id in ids)
        {
            var cost = byId.GetValueOrDefault(id)?.UnitCost;
            if (cost is not decimal unit || unit == 0m) continue;
            var price = SqlServerCostOperations.PriceFromMarkup(unit, percent, wholeRubles: false);
            edits.Add((id, price.ToString("0.##", CultureInfo.InvariantCulture)));
        }
        return edits;
    }
}
