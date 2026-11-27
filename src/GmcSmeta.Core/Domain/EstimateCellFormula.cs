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

    public static IReadOnlyList<int> References(string? expression) =>
        Token.Matches(expression ?? "").Select(m => int.TryParse(m.Groups[1].Value, out var n) ? n : 0)
            .Where(n => n > 0).Distinct().ToArray();

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
