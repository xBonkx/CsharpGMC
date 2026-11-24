namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure filter predicates from Python 0.66.4 ui/proc_view.py: FILTERS,
/// FILTER_SET and the late/shop/problem branches of _apply_filter.
/// Works on the already loaded SQL Server procurement projection only.
/// </summary>
public static class ProcurementViewFilter
{
    public static bool Matches(string category, string status, string stage,
        bool isWork, IReadOnlyList<string> flags, string source = "buy")
    {
        ArgumentNullException.ThrowIfNull(flags);

        return category switch
        {
            "all" => true,
            "none" => status == "none",
            "work" => status is "ordered" or "invoice" or "paid" or "shop_queued" or "shop_work",
            "late" => flags.Any(flag =>
                flag.StartsWith("просрочена поставка", StringComparison.OrdinalIgnoreCase)),
            // Original Python uses procurement.source, not the derived status:
            // a fully received workshop item stays in the shop filter.
            "shop" => source == "shop",
            "stock" => status is "stock" or "part_stock",
            "issued" => status is "issued" or "part_issued",
            "problem" => !isWork && flags.Count > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(category), category,
                "Unknown procurement filter category.")
        };
    }
}
