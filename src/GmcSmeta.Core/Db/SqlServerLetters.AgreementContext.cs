using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public sealed partial class SqlServerLetters
{
    // Called only for a newly created or explicitly attached draft, inside the
    // same serializable transaction as the context/revision checks and insert.
    // Object-only and legacy schemas keep the existing behavior.
    private static void ApplyAgreementContext(SqlConnection conn, SqlTransaction tx,
        LetterDocument document, long objectId, bool useAgreementRecipient)
    {
        if (document.ContractId is not long contractId || !SqlServerAgreements.Enabled(conn, tx))
            return;

        using var command = new SqlCommand("""
            SELECT a.id,a.number,a.agreement_date,a.customer_full,a.cust_org,
                   c.kind,c.name,c.annex_number,c.number
            FROM dbo.contracts c WITH(HOLDLOCK)
            JOIN dbo.agreements a WITH(HOLDLOCK) ON a.id=c.agreement_id AND a.object_id=c.object_id
            WHERE c.id=@contract AND c.object_id=@object
              AND c.kind IN(N'main',N'dop') AND a.archived=0
            """, conn, tx);
        command.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        command.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException("У приложения или ДС нет действующего юридического договора. Проверьте привязку.");
        string Text(int index) => reader.IsDBNull(index) ? "" :
            Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
        var agreementId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        var date = reader.IsDBNull(2) ? "" : Convert.ToDateTime(reader.GetValue(2), CultureInfo.InvariantCulture)
            .ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        LetterContext.ApplyAgreementSnapshot(document, agreementId, Text(1), date,
            Text(3), Text(4), Text(5), Text(6), Text(7), Text(8), useAgreementRecipient);
    }
}
