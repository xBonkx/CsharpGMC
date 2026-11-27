using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerKsPrintData
{
    // SQL Server permits 2100 parameters per command. Use bounded chunks
    // and retain the original per-object constraint for every requested ID.
    internal static IEnumerable<SqlServerKsContractScope.Document[]> ContractorBatches(
        IReadOnlyList<SqlServerKsContractScope.Document> documents)
    {
        if (documents.Select(d => d.Id).Distinct().Count() != documents.Count)
            throw new InvalidOperationException("Повторяющийся договор для КС-2.");
        return documents.GroupBy(d => d.ObjectId).SelectMany(g => g.Chunk(500));
    }

    internal static void RequireContractorCoverage(
        IReadOnlyList<SqlServerKsContractScope.Document> documents,
        IReadOnlyDictionary<long, Contractor> contractors)
    {
        foreach (var doc in documents)
            if (!contractors.ContainsKey(doc.Id))
                throw new InvalidOperationException("Не найден договор для КС-2.");
    }

    private static Dictionary<long, Contractor> ReadContractors(SqlConnection conn,
        IReadOnlyList<SqlServerKsContractScope.Document> documents)
    {
        var contractors = new Dictionary<long, Contractor>(documents.Count);
        foreach (var batch in ContractorBatches(documents))
        {
            var parameters = batch.Select((_, i) => "@id" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
            using var cmd = new SqlCommand(
                "SELECT c.id,c.[date],co.name,co.legal_full,co.signer_post,co.signer_name " +
                "FROM dbo.contracts c LEFT JOIN dbo.companies co ON co.id=c.company_id " +
                "WHERE c.object_id=@obj AND c.id IN (" + string.Join(",", parameters) + ")", conn);
            cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = batch[0].ObjectId;
            for (var i = 0; i < batch.Length; i++)
                cmd.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = batch[i].Id;
            using var r = cmd.ExecuteReader();
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
            {
                var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                contractors.Add(id, new Contractor(id, S(1), S(2), S(3), S(4), S(5)));
            }
        }
        // A missing contract must fail the entire package, just as before.
        RequireContractorCoverage(documents, contractors);
        return contractors;
    }
}
