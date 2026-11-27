using System.Text;

namespace GmcSmeta.Core.Domain;

/// <summary>Literal search terms for the local DataView; no SQL or editable fields.</summary>
public static class StageTableSearch
{
    public const string Column = "__Search";

    public static string Expression(string? text) =>
        string.Join(" AND ", (text ?? "").ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => $"[{Column}] LIKE '%{Escape(word)}%'"));

    private static string Escape(string word)
    {
        var result = new StringBuilder(word.Length);
        foreach (var ch in word)
            result.Append(ch switch
            {
                '\'' => "''",
                '[' => "[[]",
                ']' => "[]]",
                '%' => "[%]",
                '*' => "[*]",
                _ => ch.ToString()
            });
        return result.ToString();
    }
}
