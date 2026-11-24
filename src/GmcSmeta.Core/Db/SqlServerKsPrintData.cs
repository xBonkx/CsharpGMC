using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only snapshot for a multi-sheet KS-2 / KS-3 Excel package.
/// A single selected legal contract defines the scope; no arbitrary act IDs
/// may be used to read data from other objects.
/// </summary>
public static class SqlServerKsPrintData
{
    public sealed record Line(long ActId, string Section, string Name, string Unit,
        string PositionNumber, decimal Quantity, decimal Price, decimal WorkPrice,
        bool OutsideKp, string Basis = "", string WorkCode = "");

    public sealed record Contractor(long DocumentId, string ContractDate,
        string CompanyName, string LegalName, string SignerPosition, string SignerName);

    public sealed record Package(SqlServerKsContractScope.Scope Scope,
        KsGroupReport Report, SqlServerKsRequisites.Values Requisites,
        IReadOnlyList<Line> Lines, IReadOnlyDictionary<long, Contractor> Contractors);

    public static Package Read(string connectionString, long contractId, string groupKey)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlServerDatabase.Revision(conn);
            var scope = SqlServerKsContractScope.Read(conn, contractId);
            var package = ReadPackage(conn, contractId, scope, KsGroupReport.Calculate(scope, groupKey));
            if (!string.IsNullOrWhiteSpace(before) && before == SqlServerDatabase.Revision(conn))
                return package;
        }
        throw new InvalidOperationException("КС изменились во время подготовки экспорта. Повторите выгрузку.");
    }

    public static Package ReadSingle(string connectionString, long contractId, long actId, string expectedRevision)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var before = SqlServerDatabase.Revision(conn);
        if (string.IsNullOrWhiteSpace(before) || before != expectedRevision)
            throw new InvalidOperationException("КС изменились. Обновите акт перед выгрузкой.");
        var scope = SqlServerKsContractScope.Read(conn, contractId);
        var report = KsGroupReport.CalculateSingle(scope, actId);
        var package = ReadPackage(conn, contractId, scope, report);
        if (before != SqlServerDatabase.Revision(conn))
            throw new InvalidOperationException("КС изменились во время подготовки экспорта. Обновите акт.");
        return package;
    }

    private static Package ReadPackage(SqlConnection conn, long contractId,
        SqlServerKsContractScope.Scope scope, KsGroupReport report)
    {
        var requisites = SqlServerKsRequisites.Read(conn, contractId);
        if (requisites.ObjectId != scope.Documents[0].ObjectId)
            throw new InvalidOperationException("Реквизиты не относятся к объекту выбранной КС-3.");
        if (report.Acts.Count == 0 || report.Acts.Count > 500)
            throw new InvalidOperationException("Неверное число актов для экспорта.");

        var contractors = new Dictionary<long, Contractor>();
        foreach (var doc in scope.Documents)
        {
            using var cmd = new SqlCommand(
                "SELECT c.[date],co.name,co.legal_full,co.signer_post,co.signer_name " +
                "FROM dbo.contracts c LEFT JOIN dbo.companies co ON co.id=c.company_id " +
                "WHERE c.id=@id AND c.object_id=@obj", conn);
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = doc.Id;
            cmd.Parameters.Add("@obj", SqlDbType.BigInt).Value = doc.ObjectId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Не найден договор для КС-2.");
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            contractors.Add(doc.Id, new Contractor(doc.Id,S(0),S(1),S(2),S(3),S(4)));
        }

        var ids = report.Acts.Select(a => a.Id).ToList();
        var parameters = ids.Select((id, i) => "@act" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var lines = new List<Line>();
        using (var command = new SqlCommand(
            "SELECT al.act_id,sy.name,p.name,p.unit,p.num,al.qty,al.price,al.work_price,p.out_of_kp,p.basis,p.extra_json " +
            "FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE al.act_id IN (" + string.Join(",",parameters) + ") " +
            "AND sy.contract_id=a.contract_id AND a.contract_id IN (" +
            string.Join(",", scope.Documents.Select((d,i) => "@doc"+i)) + ") " +
            "ORDER BY a.id,sy.[order],sy.id,p.[order],p.id", conn))
        {
            for (var i=0;i<ids.Count;i++)
                command.Parameters.Add(parameters[i], SqlDbType.BigInt).Value = ids[i];
            for (var i=0;i<scope.Documents.Count;i++)
                command.Parameters.Add("@doc"+i,SqlDbType.BigInt).Value = scope.Documents[i].Id;
            using var r = command.ExecuteReader();
            string S(int i) => r.IsDBNull(i) ? "" :
                Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture));
            while(r.Read())
            {
                var basis=S(9);
                var workCode=basis;
                if(string.IsNullOrWhiteSpace(workCode) && !string.IsNullOrWhiteSpace(S(10)))
                {
                    try
                    {
                        using var json=JsonDocument.Parse(S(10));
                        if(json.RootElement.ValueKind==JsonValueKind.Object &&
                           json.RootElement.TryGetProperty("B",out var code) &&
                           code.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            workCode=code.ToString();
                    }
                    catch(JsonException) { /* The original basis remains usable. */ }
                }
                lines.Add(new Line(
                    Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),
                    S(1),S(2),S(3),S(4),D(5),D(6),D(7),
                    !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8),CultureInfo.InvariantCulture),
                    basis,workCode));
            }
        }

        return new Package(scope,report,requisites,lines,contractors);
    }
}
