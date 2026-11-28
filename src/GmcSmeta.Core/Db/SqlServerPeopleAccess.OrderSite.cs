using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed partial class SqlServerPeopleAccess
{
    // Orders use legal context, whereas admission lists are object-scoped.
    // Read-only: never link documents or infer legal ID from number/date.
    public AccessSite OrderSite(long objectId, long? documentId = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = Open(token);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        var before = Revision();
        var site = Site(objectId, token);
        if (SqlServerAgreements.Enabled(conn))
        {
            // For an explicit annex/DS require an active same-object mapping.
            // For object-only commands, auto-fill only if exactly one active
            // agreement exists. Never use TOP(1) or equal contract numbers.
            using var cmd = new SqlCommand("""
                SELECT a.id,a.number,a.agreement_date,a.company_id,a.customer_full,a.cust_org
                FROM dbo.agreements a
                WHERE a.object_id=@object AND a.archived=0
                  AND (@document IS NULL OR EXISTS(
                      SELECT 1 FROM dbo.contracts c
                      WHERE c.id=@document AND c.object_id=a.object_id
                        AND c.agreement_id=a.id AND c.kind IN(N'main',N'dop')))
                ORDER BY a.id
                """, conn);
            cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            cmd.Parameters.Add("@document", SqlDbType.BigInt).Value = documentId is long id ? id : DBNull.Value;
            var choices = new List<OrderAgreementPolicy.Agreement>();
            using (var registration = SqlReadCancellation.Register(cmd, token))
            using (var r = SqlReadCancellation.ExecuteReader(cmd, token))
            {
                while (r.Read())
                {
                    token.ThrowIfCancellationRequested();
                    string S(int i) => r.IsDBNull(i) ? "" :
                        Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    choices.Add(new OrderAgreementPolicy.Agreement(
                        Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture), S(1),
                        r.IsDBNull(2) ? "" : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture)
                            .ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                        r.IsDBNull(3) ? null : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
                        S(4), S(5)));
                    if (choices.Count == 2) break; // Enough to prove ambiguity.
                }
            }
            site = OrderAgreementPolicy.Resolve(site, choices);
        }
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(before) || before != Revision())
            throw new DBConcurrencyException("Данные договора изменились при чтении. Откройте приказы заново.");
        return site;
    }
}
