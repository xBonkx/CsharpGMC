using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>Detached append; nothing in the open editor is modified before a confirmed save.</summary>
public static class PtoVorImport
{
    public static PtoPackage Prepare(PtoPackage original,PtoAttachment attachment,IReadOnlyList<PtoWork> rows)
    {
        if(rows.Count==0||rows.Any(r=>r.AttachmentId.HasValue))
            throw new ArgumentException("ВОР должна содержать новые непривязанные строки.");
        if(original.Positions.Count+ (long)rows.Count>50000||original.Attachments.Count>=2000)
            throw new ArgumentException("Слишком много вложений или работ.");
        if(original.Attachments.Any(a=>a.Id==attachment.Id||
            a.Sha256.Equals(attachment.Sha256,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Этот файл уже добавлен в комплект.");
        var acts=rows.Where(r=>r.SourceActId.HasValue).Select(r=>r.SourceActId).ToHashSet();
        if(original.Positions.Any(r=>r.SourceActId.HasValue&&acts.Contains(r.SourceActId)))
            throw new InvalidOperationException("ВОР этого акта уже добавлена в комплект.");
        var candidate=JsonSerializer.Deserialize<PtoPackage>(JsonSerializer.Serialize(original,PtoRules.Json),PtoRules.Json)!;
        candidate.Revision=original.Revision;
        candidate.Attachments.Add(attachment);
        foreach(var row in rows)
            candidate.Positions.Add(new PtoWork
            {
                System=row.System,Name=row.Name,Unit=row.Unit,Qty=row.Qty,
                SourceActId=row.SourceActId,SourcePositionId=row.SourcePositionId,
                SourceFile=row.SourceFile,SourceSheet=row.SourceSheet,SourceRow=row.SourceRow,
                AttachmentId=attachment.Id
            });
        candidate.Status="Черновик";
        candidate.ManualReview=false;
        PtoRules.ValidateShape(candidate);
        return candidate;
    }

    // Only called from the exclusive UI operation after SQL returned success.
    public static void Publish(PtoPackage original,PtoPackage committed)
    {
        if(original.Id!=committed.Id||original.ObjectId!=committed.ObjectId||
            original.ContractId!=committed.ContractId||committed.Revision!=original.Revision+1||
            committed.Attachments.Count!=original.Attachments.Count+1||
            committed.Positions.Count<=original.Positions.Count)
            throw new InvalidOperationException("Неверный снимок сохранённой ВОР. Перечитайте комплект.");
        original.Attachments.Add(committed.Attachments[^1]);
        original.Positions.AddRange(committed.Positions.Skip(original.Positions.Count));
        original.Status=committed.Status;
        original.ManualReview=committed.ManualReview;
        original.Revision=committed.Revision;
    }
}
