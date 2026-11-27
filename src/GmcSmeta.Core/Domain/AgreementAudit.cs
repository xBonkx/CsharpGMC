namespace GmcSmeta.Core.Domain;

/// <summary>Diagnostic only. Detects unresolved legal metadata; never groups or rewrites records.</summary>
public static class AgreementAudit
{
    public sealed record Finding(int ObjectId, string Code, IReadOnlyList<int> DocumentIds);

    public static IReadOnlyList<Finding> Inspect(IEnumerable<DocRow> source)
    {
        var findings = new List<Finding>();
        foreach (var group in source.Where(d => d.Kind is "main" or "dop").GroupBy(d => d.ObjectId).OrderBy(g => g.Key))
        {
            var documents = group.ToArray();
            var mains = documents.Where(d => d.Kind == "main").ToArray();
            void Add(string code, IEnumerable<DocRow> rows)
            {
                var ids = rows.Select(d => d.Id).Distinct().Order().ToArray();
                if (ids.Length > 0) findings.Add(new Finding(group.Key, code, ids));
            }
            if (mains.Length == 0) Add("NO_MAIN", documents);
            if (mains.Select(d => d.Number.Trim()).Distinct(StringComparer.Ordinal).Count() > 1)
                Add("MULTIPLE_NUMBERS", mains);
            Add("EMPTY_NUMBER", mains.Where(d => string.IsNullOrWhiteSpace(d.Number)));
            Add("MISSING_COMPANY", documents.Where(d => d.CompanyId is null or <= 0));
            if (documents.Where(d => d.CompanyId is > 0).Select(d => d.CompanyId).Distinct().Count() > 1)
                Add("MULTIPLE_COMPANIES", documents);
            var ids = mains.Select(d => d.Id).ToHashSet();
            Add("UNLINKED_DS", documents.Where(d => d.Kind == "dop" && d.BaseContractId is null));
            Add("INVALID_DS_BASE", documents.Where(d => d.Kind == "dop" && d.BaseContractId is int id && !ids.Contains(id)));
            if (AgreementPresentation.Resolve(documents) is null)
                Add("UNRESOLVED_GROUP", documents);
            // Existing KS scope groups by effective number inside the object. Flag
            // cases where that historical heuristic merges inconsistent main metadata.
            foreach (var sameNumber in mains.GroupBy(d => d.Number.Trim(), StringComparer.Ordinal))
            {
                var rows = sameNumber.ToArray();
                if (rows.Length > 1 && AgreementPresentation.Resolve(rows) is null)
                    Add("KS_SCOPE_REVIEW", rows);
            }
        }
        return findings;
    }
}
