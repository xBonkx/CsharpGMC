namespace GmcSmeta.Core.Domain;

/// <summary>Reusable word filter for one shipment view. Preserves the original
/// literal-space splitting and ordinal-ignore-case matching across two fields.</summary>
public sealed class ShipmentSearchQuery
{
    private string _text = "";
    private string[] _words = Array.Empty<string>();

    public bool Matches(string? query, string name, string systemLabel)
    {
        query ??= "";
        if (!string.Equals(_text, query, StringComparison.Ordinal))
        {
            _words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _text = query;
        }
        // No token contains a literal space, so no token can straddle the
        // single space formerly inserted between the two search fields.
        foreach (var word in _words)
            if (!name.Contains(word, StringComparison.OrdinalIgnoreCase) &&
                !systemLabel.Contains(word, StringComparison.OrdinalIgnoreCase))
                return false;
        return true;
    }
}
