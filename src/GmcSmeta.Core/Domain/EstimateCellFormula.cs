using System.Globalization;
using GmcSmeta.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Transient numbered picks are expanded to ordinary arithmetic before the existing SQL save.</summary>
public static class EstimateCellFormula
{
    private static readonly Regex Token = new(@"\[(\d+)\]", RegexOptions.CultureInvariant);

    public static string Read(string? json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? "";
        }
        catch (JsonException) { }
        return "";
    }

    public static IReadOnlyList<int> References(string? expression)
    {
        if (string.IsNullOrEmpty(expression)) return Array.Empty<int>();
        List<int>? references = null;
        HashSet<int>? seen = null;
        for (var match = Token.Match(expression); match.Success; match = match.NextMatch())
        {
            if (!int.TryParse(match.Groups[1].ValueSpan, out var n) || n <= 0) continue;
            seen ??= new HashSet<int>();
            if (!seen.Add(n)) continue;
            references ??= new List<int>();
            references.Add(n);
        }
        return references is null ? Array.Empty<int>() : references.ToArray();
    }

    public static string? Expand(string text, IReadOnlyDictionary<int, decimal> values)
    {
        var missing = false;
        var result = Token.Replace(text, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out var n) || !values.TryGetValue(n, out var value))
            {
                missing = true;
                return m.Value;
            }
            return "(" + value.ToString("0.############################", CultureInfo.InvariantCulture) + ")";
        });
        return missing || NumberText.Parse(result) is null ? null : result;
    }
}
