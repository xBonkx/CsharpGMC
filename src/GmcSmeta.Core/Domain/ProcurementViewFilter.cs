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
            "none" or "work" or "stock" or "issued" => StatusCategory(status) == category,
            "late" => IsLate(flags),
            // Original Python uses procurement.source, not the derived status:
            // a fully received workshop item stays in the shop filter.
            "shop" => source == "shop",
            "problem" => IsProblem(isWork, flags),
            _ => throw new ArgumentOutOfRangeException(nameof(category), category,
                "Unknown procurement filter category.")
        };
    }

    private static string StatusCategory(string status) => status switch
    {
        "none" => "none",
        "ordered" or "invoice" or "paid" or "shop_queued" or "shop_work" => "work",
        "stock" or "part_stock" => "stock",
        "issued" or "part_issued" => "issued",
        _ => ""
    };

    private static bool IsLate(IReadOnlyList<string> flags) => flags.Any(flag =>
        flag.StartsWith("просрочена поставка", StringComparison.OrdinalIgnoreCase));

    private static bool IsProblem(bool isWork, IReadOnlyList<string> flags) =>
        !isWork && flags.Count > 0;

    public readonly record struct CategoryCounts(int All, int None, int Work,
        int Late, int Shop, int Stock, int Issued, int Problem);

    /// <summary>
    /// One pass over loaded rows; late, shop and problem may overlap with
    /// status categories and with each other. Do not apply current UI filters.
    /// </summary>
    public static CategoryCounts CountCategories(IEnumerable<(
        string Status, bool IsWork, IReadOnlyList<string> Flags, string Source)> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        int all = 0, none = 0, work = 0, late = 0, shop = 0, stock = 0, issued = 0, problem = 0;
        foreach (var row in rows)
        {
            ArgumentNullException.ThrowIfNull(row.Flags);
            all++;
            switch (StatusCategory(row.Status))
            {
                case "none": none++; break;
                case "work": work++; break;
                case "stock": stock++; break;
                case "issued": issued++; break;
            }
            if (IsLate(row.Flags)) late++;
            if (row.Source == "shop") shop++;
            if (IsProblem(row.IsWork, row.Flags)) problem++;
        }
        return new(all, none, work, late, shop, stock, issued, problem);
    }
}
