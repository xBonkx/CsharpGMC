using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>Local certificate search with the same serialized fields as the SQL reader.</summary>
public static class PtoCertificateSearch
{
    public sealed record Row(PtoCertificate Certificate,string SearchText);

    public static IReadOnlyList<Row> Build(IEnumerable<PtoCertificate> certificates) =>
        certificates.Select(c=>new Row(c,JsonSerializer.Serialize(c,PtoRules.Json))).ToArray();

    public static IReadOnlyList<PtoCertificate> Filter(IReadOnlyList<Row> rows,string? query)
    {
        var text=(query??"").Trim();
        return rows.Where(row=>text.Length==0||row.SearchText.Contains(text,StringComparison.OrdinalIgnoreCase))
            .Select(row=>row.Certificate).ToArray();
    }
}
