using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Read-only index of reference numbers for one published KS sheet.
/// Built lazily on the first visible-number lookup; stable @ID references
/// do not need a reverse index. The supplied snapshot must not be mutated.
/// </summary>
public sealed class ActQuantityReferenceIndex(IReadOnlyDictionary<long, string> numbers)
{
    private static readonly Regex Reference = new(@"[КK](\d+(?:[.,]\d+)*)|@(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private Dictionary<string, long?>? _byNumber;

    private static string Normalize(string value) => value.Replace(',', '.');

    private Dictionary<string, long?> ByNumber
    {
        get
        {
            if (_byNumber is not null) return _byNumber;
            var index = new Dictionary<string, long?>(StringComparer.Ordinal);
            foreach (var (id, number) in numbers)
                if (!index.TryAdd(Normalize(number), id)) index[Normalize(number)] = null;
            return _byNumber = index;
        }
    }

    public IReadOnlyList<long> EditingReferences(string? text)
    {
        var result = new List<long>();
        if (text is null || !text.TrimStart().StartsWith('=')) return result;
        var seen = new HashSet<long>();
        foreach (Match match in Reference.Matches(text))
        {
            long id;
            if (match.Groups[2].Success)
            {
                if (!long.TryParse(match.Groups[2].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out id) || !numbers.ContainsKey(id)) continue;
            }
            else
            {
                if (!ByNumber.TryGetValue(Normalize(match.Groups[1].Value), out var unique)
                    || unique is not long value) continue;
                id = value;
            }
            if (seen.Add(id)) result.Add(id);
        }
        return result;
    }
}
