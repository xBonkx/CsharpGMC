namespace GmcSmeta.Core.Db;

[Flags]
public enum CostMissingPrice
{
    None = 0, MaterialPrice = 1, MaterialCost = 2, WorkSell = 4, WorkCost = 8
}

/// <summary>Flags and counts for one accepted SQL summary snapshot.</summary>
public sealed class CostMissingPriceIndex
{
    private readonly Dictionary<long, CostMissingPrice> _byId = new();
    private readonly int[] _histogram = new int[16];

    public CostMissingPriceIndex(IEnumerable<CostSummaryRow> rows)
    {
        foreach (var row in rows)
        {
            var flags = (row.NoPrice ? CostMissingPrice.MaterialPrice : CostMissingPrice.None)
                | (row.NoCost ? CostMissingPrice.MaterialCost : CostMissingPrice.None)
                | (row.NowSell ? CostMissingPrice.WorkSell : CostMissingPrice.None)
                | (row.NowCost ? CostMissingPrice.WorkCost : CostMissingPrice.None);
            _byId[row.Id] = _byId.GetValueOrDefault(row.Id) | flags;
            _histogram[(int)flags]++;
        }
    }

    public int Count(CostMissingPrice selected)
    {
        var count = 0;
        for (var mask = 0; mask < _histogram.Length; mask++)
            if (selected == CostMissingPrice.None || (mask & (int)selected) != 0)
                count += _histogram[mask];
        return count;
    }

    // Preserve standalone WPF pane's existing transport/extra visibility.
    public bool Includes(long id, CostMissingPrice selected) =>
        selected == CostMissingPrice.None || id < 0 ||
        (_byId.GetValueOrDefault(id) & selected) != 0;
}
