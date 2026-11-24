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

        if (selectedSystems.Count > 0 && !selectedSystems.Contains(systemId)
            && !(parentSystemId is long parent && selectedSystems.Contains(parent)))
            return false;

        if (selectedSupplier is not null &&
            !string.Equals(supplier ?? "", selectedSupplier, StringComparison.Ordinal))
            return false;

        var words = (search ?? "").Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return true;

        var sources = new[] { name, systemName, supplier ?? "",
            orderRef, invoiceRef, replacement };
        return words.All(word => sources.Any(value =>
            value.Contains(word, StringComparison.OrdinalIgnoreCase)));
    }
}
