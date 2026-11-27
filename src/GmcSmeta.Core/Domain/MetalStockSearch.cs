using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Snapshot-scoped stock search keys and checked totals, preserving display order.</summary>
public sealed class MetalStockSearch
{
    private readonly (MetalStockView Row, string Key)[] _entries;

    public MetalStockSearch(IEnumerable<MetalStockView> rows) =>
        _entries = rows.Select(row => (row, (row.Category + " " + row.Material).Replace(',', '.'))).ToArray();

    public MetalStockSelection Filter(string category, string query)
    {
        query = query.Trim().Replace(',', '.');
        var rows = new List<MetalStockView>();
        long owned = 0, ours = 0, supplier = 0, need = 0, deficit = 0;
        foreach (var (row, key) in _entries)
        {
            if (category.Length != 0 && category != row.Category) continue;
            if (query.Length != 0 && !key.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(row);
            checked
            {
                owned = owned + row.Ours + row.Supplier;
                ours += row.Ours;
                supplier += row.Supplier;
                need += row.RemainingDemand;
                deficit += row.Deficit;
            }
        }
        return new MetalStockSelection(rows, owned, ours, supplier, need, deficit);
    }
}

public sealed record MetalStockSelection(IReadOnlyList<MetalStockView> Rows,
    long Owned, long Ours, long Supplier, long Need, long Deficit);
