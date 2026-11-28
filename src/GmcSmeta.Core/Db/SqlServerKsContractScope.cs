using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// All annexes/DS of one legal agreement. Python contract_no() resolves a DS
/// through base_contract_id, or through the only main contract of the object.
/// Keeps data strictly within the selected object's SQL Server documents.
/// </summary>
public static class SqlServerKsContractScope
{
    public sealed record Document(long Id, long ObjectId, string Kind,
        string Number, string Name, long? BaseContractId, string Form,
        decimal VatRate, string SitePart, string SmrMode = "")
    {
        public string Label => Kind == "dop"
            ? ("ДС " + (string.IsNullOrWhiteSpace(Number) ? Name : "№ " + Number +
                (string.IsNullOrWhiteSpace(Name) ? "" : " · " + Name)))
            : (string.IsNullOrWhiteSpace(Name) ? "Договор № " + Number : Name);
    }

    public sealed record Scope(long SelectedDocumentId, string AgreementNumber,
        IReadOnlyList<Document> Documents, IReadOnlyList<SqlServerActs.ActInfo> Acts,
        Ks3OpeningBalance? Opening = null);

    public static string EffectiveNumber(Document doc, IReadOnlyList<Document> documents)
    {
        if (doc.Kind != "dop") return doc.Number.Trim();
        if (doc.BaseContractId is long id)
        {
            var main = documents.FirstOrDefault(x => x.Id == id && x.ObjectId == doc.ObjectId);
            if (main is not null) return main.Number.Trim();
        }
        var mains = documents.Where(x => x.ObjectId == doc.ObjectId && x.Kind == "main")
            .Select(x => x.Number.Trim()).Distinct(StringComparer.Ordinal).ToList();
        return mains.Count == 1 ? mains[0] : doc.Number.Trim();
    }

    public static IReadOnlyList<Document> ResolveDocuments(long currentDocumentId,
        IReadOnlyList<Document> all)
    {
        var selected = all.FirstOrDefault(x => x.Id == currentDocumentId)
            ?? throw new InvalidOperationException("Исходный договор не найден.");
        var number = EffectiveNumber(selected, all);
        if (number.Length == 0 && selected.Kind is not ("main" or "dop"))
            return new[] { selected };
        // Python groups by effective number within the object, including a blank number.
        return all.Where(d => d.ObjectId == selected.ObjectId &&
                (d.Kind == "main" || d.Kind == "dop") &&
                EffectiveNumber(d, all) == number)
            .OrderBy(d => d.Id).ToList();
    }

    public static Scope Read(string connectionString, long selectedDocumentId, CancellationToken token = default) =>
        ReadConsistent(connectionString, selectedDocumentId, requireOverview: false, token: token);

    // Overview totals cover all documents/acts, unlike an individual KS-3.
    public static Scope ReadOverview(string connectionString, long selectedDocumentId, CancellationToken token = default) =>
        ReadConsistent(connectionString, selectedDocumentId, requireOverview: true, token: token);

    private static Scope ReadConsistent(string connectionString, long selectedDocumentId, bool requireOverview, CancellationToken token)
    {
        using var conn = SqlReadCancellation.Open(connectionString, token);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlReadCancellation.Revision(conn, token);
            var result = Read(conn, selectedDocumentId, token: token);
            var ownership = requireOverview ? ReadLegalOwnership(conn, result, token) : null;
            if (!string.IsNullOrWhiteSpace(before) && before == SqlReadCancellation.Revision(conn, token))
            {
                if (ownership is not null)
                    KsLegalScopePolicy.RequireOverview(result, ownership);
                return result;
            }
        }
        throw new InvalidOperationException("Данные договора изменились во время чтения КС. Повторите обновление.");
    }

    internal static Scope Read(SqlConnection conn, long selectedDocumentId,int? rehearsalSchema=null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        long objectId;
        using (var read = new SqlCommand(
            "SELECT object_id FROM dbo.contracts WHERE id=@id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = selectedDocumentId;
            var value = SqlReadCancellation.Scalar(read, token);
            if (value is null || value is DBNull)
                throw new InvalidOperationException("Исходный договор не найден.");
            objectId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        var documents = new List<Document>();
        using (var command = new SqlCommand(
            "SELECT id,object_id,kind,number,name,base_contract_id,form,vat_rate,ks_site_part,smr_mode " +
            "FROM dbo.contracts WHERE object_id=@id AND kind IN (N'main',N'dop')", conn))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = objectId;
            using var cancellation = SqlReadCancellation.Register(command, token);
            using var r = SqlReadCancellation.ExecuteReader(command, token);
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                documents.Add(new Document(
                    SqlInteger.ReadInt64(r, 0),
                    SqlInteger.ReadInt64(r, 1),
                    S(2), S(3), S(4),
                    r.IsDBNull(5) ? null : SqlInteger.ReadInt64(r, 5),
                    S(6),
                    r.IsDBNull(7) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture)),
                    S(8), S(9)));
            }
        }

        var scopeDocs = ResolveDocuments(selectedDocumentId, documents);
        var sourceDoc = documents.Single(d => d.Id == selectedDocumentId);
        var labels = scopeDocs.ToDictionary(d => d.Id, d => d.Label);
        var acts = SqlServerActs.ActsOfDocuments(conn, scopeDocs.Select(d => d.Id).ToArray(),
            rehearsalSchema, token);
        for (var i = 0; i < acts.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var act = acts[i];
            acts[i] = act with { SourceName = labels[act.ContractId] };
        }
        acts.Sort((left, right) =>
        {
            int d = left.Date.CompareTo(right.Date);
            return d != 0 ? d : left.Id.CompareTo(right.Id);
        });
        var agreementNumber = EffectiveNumber(sourceDoc, documents);
        Ks3OpeningBalance? opening = null;
        // The importer stores a gross opening amount, keyed by object and base contract.
        // Read only: no SQL write or migration, and never include another object's marker.
        using (var marker = new SqlCommand(
            "SELECT [value] FROM dbo.meta WHERE [key]=@key", conn))
        {
            marker.Parameters.Add("@key", SqlDbType.NVarChar, 512).Value =
                $"ks3_opening:{objectId}:{agreementNumber}";
            var json = Convert.ToString(SqlReadCancellation.Scalar(marker, token), CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(json))
                opening = Ks3OpeningBalance.Parse(json);
        }
        return new Scope(selectedDocumentId,
            agreementNumber, scopeDocs, acts, opening);
    }

    // Null means the legacy schema has no legal IDs; an empty map on schema 32
    // means there are no valid active mappings and must not bypass validation.
    internal static IReadOnlyDictionary<long, long>? ReadLegalOwnership(SqlConnection conn, Scope scope, CancellationToken token = default)
    {
        if (!SqlServerAgreements.Enabled(conn, token: token)) return null;
        var selected = scope.Documents.Single(d => d.Id == scope.SelectedDocumentId);
        var documentIds = scope.Documents.Select(d => d.Id).ToHashSet();
        using var cmd = new SqlCommand(
            "SELECT c.id,a.id FROM dbo.contracts c JOIN dbo.agreements a " +
            "ON a.id=c.agreement_id AND a.object_id=c.object_id AND a.archived=0 " +
            "WHERE c.object_id=@object", conn);
        cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = selected.ObjectId;
        using var registration = SqlReadCancellation.Register(cmd, token);
        using var reader = SqlReadCancellation.ExecuteReader(cmd, token);
        var result = new Dictionary<long, long>();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (documentIds.Contains(id))
                result.Add(id, Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
        }
        token.ThrowIfCancellationRequested();
        return result;
    }

    public static KsGroupReport GroupReport(Scope scope, string groupKey) =>
        KsGroupReport.Calculate(scope, groupKey);
}
