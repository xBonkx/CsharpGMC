namespace GmcSmeta.Core.Domain;

/// <summary>Python attachments.direct_signed: only explicitly classified PDFs qualify.</summary>
public static class SignedPdfSelection
{
    public sealed record Entry(long Id, string Role, bool IsPrimary);

    public static long? DirectId(IEnumerable<Entry> entries)
    {
        var signed = entries.Where(entry => entry.Role == "signed_contract").ToArray();
        var primary = signed.Where(entry => entry.IsPrimary).ToArray();
        return primary.Length == 1 ? primary[0].Id :
            signed.Length == 1 ? signed[0].Id : null;
    }
}
