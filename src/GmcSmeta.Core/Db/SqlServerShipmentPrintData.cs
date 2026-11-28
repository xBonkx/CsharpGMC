using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only print data for a single existing shipment. Export never creates, fixes,
/// unfixes or changes a shipment. The object filter prevents printing another site's list.
/// </summary>
public static class SqlServerShipmentPrintData
{
    public sealed record Line(string Document, string System, string Name, string Unit,
        decimal Quantity, string Note, bool IsSub);
    public sealed record Snapshot(string Number, DateTime Date, string ObjectName,
        string Recipient, string IssuedBy, string Note, bool Fixed, IReadOnlyList<Line> Lines);

    public static Snapshot Read(SqlConnection conn, long objectId, long shipmentId,
        string? expectedRevision = null)
    {
        ArgumentNullException.ThrowIfNull(conn);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlServerDatabase.Revision(conn);
            if (string.IsNullOrWhiteSpace(before) ||
                (expectedRevision is not null && before != expectedRevision))
                throw new InvalidOperationException("Ведомость изменилась. Обновите её перед выгрузкой.");
            var snapshot = ReadCore(conn, objectId, shipmentId);
            if (before == SqlServerDatabase.Revision(conn))
                return snapshot;
            if (expectedRevision is not null) break;
        }
        throw new InvalidOperationException("Ведомость изменилась во время экспорта. Повторите выгрузку.");
    }

    private static Snapshot ReadCore(SqlConnection conn, long objectId, long shipmentId)
    {
        if (objectId <= 0 || shipmentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(shipmentId));
        string number, objectName, recipient, issuedBy, note;
        DateTime date;
        bool fixedDocument;
        using (var cmd = new SqlCommand(
            "SELECT sh.number,sh.[date],o.name,ISNULL(sh.recipient,N'')," +
            "ISNULL(sh.issued_by,N''),ISNULL(sh.note,N''),sh.fixed " +
            "FROM dbo.shipments sh JOIN dbo.objects o ON o.id=sh.object_id " +
            "WHERE sh.id=@shipment AND sh.object_id=@object", conn))
        {
            cmd.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId;
            cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("Ведомость объекта не найдена.");
            number = reader.IsDBNull(0) ? "" : reader.GetString(0);
            date = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1);
            objectName = reader.GetString(2);
            recipient = reader.GetString(3);
            issuedBy = reader.GetString(4);
            note = reader.GetString(5);
            fixedDocument = !reader.IsDBNull(6) && Convert.ToBoolean(reader.GetValue(6), CultureInfo.InvariantCulture);
        }

        var lines = new List<Line>();
        using (var cmd = new SqlCommand(
            "SELECT c.kind,ISNULL(c.number,N''),ISNULL(c.name,N''),sy.name,p.name,p.unit,sl.qty,p.parent_id,ISNULL(sl.note,N'') " +
            "FROM dbo.shipment_lines sl " +
            "JOIN dbo.positions p ON p.id=sl.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE sl.shipment_id=@shipment AND c.object_id=@object " +
            "ORDER BY c.id,sy.[order],sy.id,p.[order],p.id,sl.id", conn))
        {
            cmd.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId;
            cmd.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var docNumber = reader.GetString(1);
                var docName = reader.GetString(2);
                var document = kind switch
                {
                    "main" => "Договор",
                    "dop" => "ДС",
                    _ => "КП"
                };
                document += docNumber.Length == 0 ? "" : " № " + docNumber;
                if (docName.Length > 0) document += " — " + docName;
                lines.Add(new Line(document, reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? 0m : Convert.ToDecimal(reader.GetValue(6), CultureInfo.InvariantCulture),
                    reader.IsDBNull(8) ? "" : reader.GetString(8), !reader.IsDBNull(7)));
            }
        }
        using (var count = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id=@shipment", conn))
        {
            count.Parameters.Add("@shipment", SqlDbType.BigInt).Value = shipmentId;
            if (SqlInteger.RequireInt64(count.ExecuteScalar()) != lines.Count)
                throw new InvalidOperationException("Ведомость содержит строки вне объекта или повреждённые ссылки. Экспорт отменён.");
        }
        return new Snapshot(number, date, objectName, recipient, issuedBy, note, fixedDocument, lines);
    }
}
