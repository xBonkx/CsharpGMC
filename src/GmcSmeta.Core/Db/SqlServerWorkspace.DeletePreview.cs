using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

public static partial class SqlServerWorkspace
{
    public sealed record ContractDeleteInfo(string Title, string ObjectName, int Acts, int Movements,
        IReadOnlyList<string> Details);

    public sealed record ObjectDeleteInfo(string Name, IReadOnlyList<string> Details);

    /// <summary>Что удаляется вместе с документом — текст подтверждений (kp_editor.delete_contract).</summary>
    public static ContractDeleteInfo DescribeContractForDelete(SqlConnection conn, SqlTransaction? tx, long id)
    {
        string title, objectName;
        using (var cmd = new SqlCommand(
            "SELECT c.kind,c.number,c.[date],c.name,o.name FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Документ не найден.");
            string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            title = Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3));
            objectName = Text(4);
        }
        int acts;
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.acts WHERE contract_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            acts = (int)Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        int movements;
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.movements m JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems s ON s.id=p.system_id WHERE s.contract_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            movements = (int)Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        var details = WorkspaceDeletePresentation.ContractDetails(acts, movements);
        return new ContractDeleteInfo(title, objectName, acts, movements, details);
    }

    /// <summary>Что удаляется вместе с объектом — текст первого подтверждения (main_window.delete_object).</summary>
    public static ObjectDeleteInfo DescribeObjectForDelete(SqlConnection conn, SqlTransaction? tx, long id)
    {
        string name;
        using (var cmd = new SqlCommand("SELECT name FROM dbo.objects WHERE id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var value = cmd.ExecuteScalar();
            if (value is null or DBNull) throw new ArgumentException("Объект не найден.");
            name = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
        var details = new List<string>();
        var titles = new List<string>();
        using (var cmd = new SqlCommand(
            "SELECT kind,number,[date],name FROM dbo.contracts WHERE object_id=@id ORDER BY [order],id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            while (r.Read())
                titles.Add(Documents.ContractTitleText(
                    r.IsDBNull(0) ? "kp" : r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3)));
        }
        if (WorkspaceDeletePresentation.DocumentDetails(titles) is { } documentDetails)
            details.Add(documentDetails);
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*), COALESCE(SUM(CASE WHEN signed=1 THEN 1 ELSE 0 END), 0) FROM dbo.acts " +
            "WHERE contract_id IN (SELECT id FROM dbo.contracts WHERE object_id=@id)", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            using var r = cmd.ExecuteReader();
            // SUM по int возвращает int: читаем через Convert, не GetInt64.
            if (r.Read() && Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture) > 0)
            {
                var acts = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var signed = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                details.Add(WorkspaceDeletePresentation.ActDetails(acts, signed));
            }
        }
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.shipments WHERE object_id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count > 0) details.Add("ведомости выдачи: " + count);
        }
        using (var cmd = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id IN (" +
            "SELECT id FROM dbo.positions WHERE system_id IN (" +
            "SELECT id FROM dbo.systems WHERE contract_id IN " +
            "(SELECT id FROM dbo.contracts WHERE object_id=@id)))", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            var count = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (count > 0) details.Add($"история закупок и выдачи: {count} записей");
        }
        return new(name, details);
    }

}
