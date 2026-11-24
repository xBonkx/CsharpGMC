using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Python acts.next_number across a main contract and its DS documents.</summary>
public static class KsActNumbering
{
    public static string NextNumber(IReadOnlyList<SqlServerActs.ActInfo> local,
        IReadOnlyList<SqlServerActs.ActInfo> legalAgreement, bool otherDocumentHasActs)
    {
        if (otherDocumentHasActs)
        {
            var groups=KsWorkspaceProjection.Build(legalAgreement);
            if (groups.Count>0)
            {
                var latest=groups[^1];
                if (int.TryParse(latest.Key,out _) && latest.Acts.Any(a=>!a.Signed))
                    return latest.Key+"."+(latest.Acts.Max(a=>Suffix(a.Number))+1);
                var keys=groups.Select(g=>int.TryParse(g.Key,out var value) ? (int?)value:null)
                    .Where(x=>x.HasValue).Select(x=>x!.Value).ToList();
                if (keys.Count>0)
                    return (keys.Max()+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        int max=0;
        foreach (var act in local)
        {
            var text=(act.Number??"").Split('.',2)[0].Trim('№',' ');
            if (int.TryParse(text,out var number))
                max=Math.Max(max,number);
        }
        return (max+1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int Suffix(string number)
    {
        var parts=(number??"").Split('.',2);
        return parts.Length>1 && int.TryParse(parts[1].Trim(),out var value) ? value : 1;
    }
}
