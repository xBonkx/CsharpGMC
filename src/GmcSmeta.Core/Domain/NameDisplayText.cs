namespace GmcSmeta.Core.Domain;

/// <summary>Presentation-only conversion for the compact name display.</summary>
public static class NameDisplayText
{
    public static string SingleLine(string? value) =>
        (value ?? "").Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ').Replace('\n', ' ')
            .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ')
            .Replace('\v', ' ').Replace('\f', ' ');
}
