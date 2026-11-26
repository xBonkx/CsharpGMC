namespace GmcSmeta.Core.Domain;

/// <summary>
/// Read-only, SQL-free procurement scope predicates. System selection follows
/// Python SystemPicker: an empty selection means all systems, selecting a parent
/// includes its direct subsections; supplier empty string means "(не указан)".
/// Search keeps the Python word-by-word AND rule and retains the extra WPF
/// searchable procurement fields already available to the operator.
/// </summary>
public static class ProcurementScopeFilter
{
    public static bool Matches(long systemId, long? parentSystemId, string systemName,
        string? supplier, IReadOnlyCollection<long> selectedSystems, string? selectedSupplier,
        string? search, string name, string orderRef, string invoiceRef, string replacement)
    {
        ArgumentNullException.ThrowIfNull(selectedSystems);
        return MatchesPrepared(systemId, parentSystemId, systemName, supplier, selectedSystems,
            selectedSupplier, PrepareSearch(search), name, orderRef, invoiceRef, replacement);
    }

    public static string[] PrepareSearch(string? search) =>
        (search ?? "").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Use the same prepared words for every row of a single filter pass.</summary>
    public static bool MatchesPrepared(long systemId, long? parentSystemId, string systemName,
        string? supplier, IReadOnlyCollection<long> selectedSystems, string? selectedSupplier,
        IReadOnlyList<string> words, string name, string orderRef, string invoiceRef, string replacement)
    {
        ArgumentNullException.ThrowIfNull(selectedSystems);
        ArgumentNullException.ThrowIfNull(words);

        if (selectedSystems.Count > 0 && !selectedSystems.Contains(systemId)
            && !(parentSystemId is long parent && selectedSystems.Contains(parent)))
            return false;

        if (selectedSupplier is not null &&
            !string.Equals(supplier ?? "", selectedSupplier, StringComparison.Ordinal))
            return false;

        foreach (var word in words)
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                systemName.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                (supplier ?? "").Contains(word, StringComparison.OrdinalIgnoreCase) ||
                orderRef.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                invoiceRef.Contains(word, StringComparison.OrdinalIgnoreCase) ||
                replacement.Contains(word, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }
}
