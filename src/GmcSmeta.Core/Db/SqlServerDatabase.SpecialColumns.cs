using System.Data;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// SQL Server-only editor for the Python m² / "Обоснование" columns.
/// Parent m² summary quantities follow their children in the *same* tx.
/// The new feature is pilot-gated until live transaction acceptance.
/// </summary>
public static partial class SqlServerDatabase
{
    public static SaveResult SaveSpecialCell(string connectionString, long positionId,
        string field, string input, string expectedRevision)
    {
        if (positionId <= 0 || field is not ("area_manual" or "basis"))
            return SaveResult.Fail("Недопустимая позиция или специальное поле.", expectedRevision);
        input ??= "";
        var trimmed = input.Trim();
        if (field == "basis" && trimmed.Length > 64)
            trimmed = trimmed[..64]; // Python update_position(... basis=text[:64]).
        decimal? manual = null;
        if (field == "area_manual" &&
            !AreaCellPresentation.TryParseManual(input, out manual))
            return SaveResult.Fail("Некорректная площадь м² на единицу.", expectedRevision);

        string database;
        try { database = new SqlConnectionStringBuilder(connectionString).InitialCatalog; }
        catch (ArgumentException)
        {
            return SaveResult.Fail("Некорректная строка подключения SQL Server.", expectedRevision);
        }
        if (!database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase))
            return SaveResult.Fail(
                "Новые колонки м² и «Обоснование» пока редактируются только в тестовой базе GmcSmetaPilot_*.",
                expectedRevision);

        using var conn = Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var current = LockRevision(conn, tx);
        if (current is null) return SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
            return SaveResult.Fail("Смета изменена другим пользователем. Перечитай документ.", current);

        long contractId, objectId;
        string contractName, systemName, rowName, oldBasis, form, tagRules, extra;
        double? oldManual;
        bool locked;
        using (var cmd = new SqlCommand(
            "SELECT c.id, c.object_id, c.name, c.locked, c.form, t.rules, " +
            "sy.name, p.name, p.basis, p.area_manual, p.extra_json " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "LEFT JOIN dbo.tags t ON t.[key]=c.form WHERE p.id=@id", conn, tx))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return SaveResult.Fail("Позиция не найдена.", current);
            contractId = SqlInteger.ReadInt64(r, 0);
            objectId = SqlInteger.ReadInt64(r, 1);
            contractName = r.IsDBNull(2) ? "" : r.GetString(2);
            locked = !r.IsDBNull(3) && SqlFlag.Read(r, 3);
            form = r.IsDBNull(4) ? "" : r.GetString(4);
            tagRules = r.IsDBNull(5) ? "" : r.GetString(5);
            systemName = r.IsDBNull(6) ? "" : r.GetString(6);
            rowName = r.IsDBNull(7) ? "" : r.GetString(7);
            oldBasis = r.IsDBNull(8) ? "" : r.GetString(8);
            oldManual = r.IsDBNull(9) ? null :
                Convert.ToDouble(r.GetValue(9), CultureInfo.InvariantCulture);
            extra = r.IsDBNull(10) ? "" : r.GetString(10);
        }
        if (locked)
            return SaveResult.Fail("Подписанную смету редактировать нельзя.", current);

        if (field == "area_manual" && Area.IsSummary(extra))
            return SaveResult.Fail("В сводной строке м² площадь рассчитывается по вложенным строкам.", current);

        if (field == "basis" &&
            !AreaCellPresentation.IsBasisAllowed(form, BasisRule(tagRules)))
            return SaveResult.Fail("У этой формы сметы нет колонки «Обоснование».", current);

        var change = field == "area_manual" ?
            (oldManual is null) != (manual is null) ||
            (manual is not null && oldManual is double prev &&
                Math.Abs(prev - (double)manual.Value) > 1e-9) :
            !string.Equals(oldBasis, trimmed, StringComparison.Ordinal);
        if (!change)
        {
            tx.Commit();
            return new SaveResult(true, null, current);
        }

        using (var update = new SqlCommand(field == "basis"
            ? "UPDATE dbo.positions SET basis=@value WHERE id=@id"
            : "UPDATE dbo.positions SET area_manual=@value WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            if (field == "basis")
                update.Parameters.Add("@value", SqlDbType.NVarChar, 64).Value = trimmed;
            else
                update.Parameters.Add("@value", SqlDbType.Float).Value =
                    manual is decimal v ? (object)(double)v : DBNull.Value;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Позиция исчезла при записи площади.");
        }

        if (field == "area_manual")
            RefreshAreaParent(conn, tx, positionId, contractId, objectId,
                contractName, systemName);

        var before = field == "basis" ? oldBasis :
            oldManual?.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
        var after = field == "basis" ? trimmed :
            manual?.ToString(CultureInfo.InvariantCulture) ?? "";
        LogPositionChange(conn, tx, positionId, contractId, objectId, "update",
            $"{contractName} / {systemName} / {rowName}: {field}: «{before}» → «{after}»");

        var newRevision = BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SaveResult(true, null, newRevision);
    }

    private static bool BasisRule(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var rules = JsonDocument.Parse(json);
            return rules.RootElement.ValueKind == JsonValueKind.Object &&
                rules.RootElement.TryGetProperty("basis", out var basis) &&
                basis.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Python area.refresh_summary: when a child changes its name, unit,
    /// quantity or manual m², recalculate its summary's qty. This also runs
    /// for preexisting SQL SaveCell writes, not just the new area editor.
    /// </summary>
    private static void RefreshAreaParent(SqlConnection conn, SqlTransaction tx,
        long positionId, long contractId, long objectId,
        string contractName, string systemName)
    {
        long? parentId = null;
        string? parentExtra = null;
        decimal oldQty = 0m;
        string parentName = "";
        using (var select = new SqlCommand(
            "SELECT p.parent_id,par.extra_json,par.qty,par.name " +
            "FROM dbo.positions p JOIN dbo.positions par ON par.id=p.parent_id " +
            "WHERE p.id=@id", conn, tx))
        {
            select.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            using var r = select.ExecuteReader();
            if (r.Read())
            {
                parentId = SqlInteger.ReadInt64(r, 0);
                parentExtra = r.IsDBNull(1) ? null : r.GetString(1);
                oldQty = r.IsDBNull(2) ? 0m :
                    Convert.ToDecimal(r.GetValue(2), CultureInfo.InvariantCulture);
                parentName = r.IsDBNull(3) ? "" : r.GetString(3);
            }
        }
        if (parentId is not long summaryId || !Area.IsSummary(parentExtra))
            return;

        var values = new List<(string Name, string Unit, double Qty, double? Manual)>();
        using (var children = new SqlCommand(
            "SELECT name,unit,qty,area_manual FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE parent_id=@id", conn, tx))
        {
            children.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            using var r = children.ExecuteReader();
            while (r.Read())
                values.Add((
                    r.IsDBNull(0) ? "" : r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0d :
                        Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null :
                        Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)));
        }
        var next = AreaCellPresentation.SummaryQuantity(values);
        if (Math.Abs(oldQty - next) <= 0.0000005m) return;

        // Updating an m² summary's quantity changes the parent KP money.
        // Refuse when that row is used in signed acts, fixed shipments,
        // recorded issues or assigned stages: no silent downstream drift.
        using (var protectedActs = new SqlCommand(
            "SELECT TOP (1) al.position_id FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "WHERE al.position_id=@id AND a.signed=1", conn, tx))
        {
            protectedActs.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            if (protectedActs.ExecuteScalar() is not null)
                throw new InvalidOperationException("Сводная строка м² используется в подписанной КС-2.");
        }
        using (var fixedShip = new SqlCommand(
            "SELECT TOP (1) sl.position_id FROM dbo.shipment_lines sl " +
            "JOIN dbo.shipments sh ON sh.id=sl.shipment_id " +
            "WHERE sl.position_id=@id AND sh.fixed=1", conn, tx))
        {
            fixedShip.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            if (fixedShip.ExecuteScalar() is not null)
                throw new InvalidOperationException("Сводная строка м² используется в закреплённой ведомости.");
        }
        using (var issued = new SqlCommand(
            "SELECT TOP (1) position_id FROM dbo.movements " +
            "WHERE position_id=@id AND shipment_id IS NOT NULL", conn, tx))
        {
            issued.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            if (issued.ExecuteScalar() is not null)
                throw new InvalidOperationException("Для сводной строки м² есть проведённая выдача.");
        }
        using (var stages = new SqlCommand(
            "SELECT TOP(1) p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE p.stage_src=@id AND c.stage_of=@full", conn, tx))
        {
            stages.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            stages.Parameters.Add("@full", SqlDbType.BigInt).Value = contractId;
            if (stages.ExecuteScalar() is not null)
                throw new InvalidOperationException("Сводная строка м² уже распределена по этапам.");
        }

        using (var update = new SqlCommand(
            "UPDATE dbo.positions SET qty=@qty WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = summaryId;
            update.Parameters.Add("@qty", SqlDbType.Float).Value = (double)next;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Сводная строка м² удалена другим пользователем.");
        }
        LogPositionChange(conn, tx, summaryId, contractId, objectId, "update",
            $"{contractName} / {systemName} / {parentName}: обновлена площадь сводной строки до {next}");
    }
}
