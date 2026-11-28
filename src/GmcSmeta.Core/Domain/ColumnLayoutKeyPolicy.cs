namespace GmcSmeta.Core.Domain;

/// <summary>Stable per-table file name, including namespaced keys.
/// Each dot separates nonempty ASCII segments; no path or traversal syntax.</summary>
public static class ColumnLayoutKeyPolicy
{
    public static bool IsValid(string? key) =>
        !string.IsNullOrWhiteSpace(key) &&
        key.Split('.').All(segment => segment.Length > 0 &&
            segment.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_'));
}
