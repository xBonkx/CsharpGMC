using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>Read-only list projection. Search keeps the existing serialized-payload
/// semantics but does not reserialize or validate every package on each keystroke.</summary>
public static class PtoPackageListProjection
{
    public sealed record Row(Guid Id,string ObjectLabel,string Section,string Number,
        string Date,string Status,int Attachments,string Findings,string SearchText);

    public static IReadOnlyList<Row> Build(IEnumerable<PtoPackage> packages,
        long? objectId=null,long? contractId=null,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var rows=new List<Row>();
        foreach(var p in packages)
        {
            token.ThrowIfCancellationRequested();
            if(objectId.HasValue && p.ObjectId!=objectId ||
                contractId.HasValue && p.ContractId!=contractId)continue;
            var findings=PtoRules.Check(p);
            rows.Add(new Row(p.Id,PtoRules.Text(p,"object")+" / "+p.ContractLabel,
                PtoRules.Text(p,"section"),PtoRules.Text(p,"number"),
                PtoRules.Text(p,"date"),p.Status,p.Attachments.Count,
                $"{findings.Errors.Count} ошибок · {findings.Warnings.Count} предупрежд.",
                JsonSerializer.Serialize(p,PtoRules.Json)));
        }
        token.ThrowIfCancellationRequested();
        return rows.ToArray();
    }

    public static IReadOnlyList<Row> Filter(IReadOnlyList<Row> rows,string query,string? status)=>
        rows.Where(p=>
            (string.IsNullOrWhiteSpace(query)||p.SearchText.Contains(query,StringComparison.OrdinalIgnoreCase))&&
            (status is null or "Все статусы" ||p.Status==status)).ToArray();
}
