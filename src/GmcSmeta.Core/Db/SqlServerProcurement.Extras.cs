using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Extra purchases are procurement records, never priced estimate rows until moved to a dop.</summary>
public static partial class SqlServerProcurement
{
    public sealed record ExtraResult(bool Saved, long? Id, string? Error, string Revision)
    {
        public static ExtraResult Fail(string message, string revision) => new(false, null, message, revision);
    }

    public static string? ValidateExtra(string? name, string? unit, decimal qty, decimal? cost = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 255)
            return "Название дополнительной закупки должно содержать 1–255 символов.";
        if (string.IsNullOrWhiteSpace(unit) || unit.Trim().Length > 32)
            return "Единица измерения должна содержать 1–32 символа.";
        if (qty <= 0m || qty > 1_000_000_000m || decimal.Round(qty, 6) != qty)
            return "Количество должно быть положительным (точность до 6 знаков).";
        if (cost is < 0m or > 1_000_000_000m)
            return "Закупочная цена должна быть неотрицательной.";
        return null;
    }

    /// <summary>
    /// Python add_out_of_kp. One transaction creates optional "Вне КП" system, position,
    /// optional procurement cost, log and revision. Does not affect KP or signed KS totals.
    /// </summary>
    public static ExtraResult AddExtra(string connectionString, int contractId, long? targetSystemId,
        string name, string unit, decimal qty, decimal? purchaseCost, string expectedRevision)
    {
        var validation = ValidateExtra(name, unit, qty, purchaseCost);
        if (validation is not null) return ExtraResult.Fail(validation, expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return ExtraResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return ExtraResult.Fail("Данные изменены. Обновите закупки.", rev);
        var objectId = 0L;
        using (var contract = new SqlCommand(
            "SELECT c.object_id,c.locked FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE c.id=@id AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", conn, tx))
        {
            contract.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var reader = contract.ExecuteReader();
            if (!reader.Read()) return ExtraResult.Fail("Договор не найден.", rev);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            if (Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture))
                return ExtraResult.Fail("Подписанный документ заблокирован.", rev);
        }
        var systemId = targetSystemId ?? 0L;
        if (systemId == 0)
        {
            using var find = new SqlCommand(
                "SELECT TOP(1) id FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE contract_id=@id AND name=N'Вне КП' ORDER BY id", conn, tx);
            find.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            var value = find.ExecuteScalar();
            if (value is not null and not DBNull)
                systemId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            else
            {
                using var create = new SqlCommand(
                    "INSERT dbo.systems (contract_id,name,[order],parent_id,smr_amount,heading,source_num,stage_sys_src) " +
                    "OUTPUT INSERTED.id VALUES (@id,N'Вне КП'," +
                    "(SELECT COALESCE(MAX([order]),-1)+1 FROM dbo.systems WHERE contract_id=@id)," +
                    "NULL,NULL,N'',N'',NULL)", conn, tx);
                create.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                systemId = Convert.ToInt64(create.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
        using (var scope = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.systems WITH (UPDLOCK,HOLDLOCK) WHERE id=@sys AND contract_id=@contract", conn, tx))
        {
            scope.Parameters.Add("@sys", SqlDbType.BigInt).Value = systemId;
            scope.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (Convert.ToInt64(scope.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                return ExtraResult.Fail("Выбранная система не принадлежит договору.", rev);
        }
        long id;
        using (var insert = new SqlCommand(
            "INSERT dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) " +
            "OUTPUT INSERTED.id VALUES (@system,NULL,NULL,N'',N'',@name,@unit,@qty,0," +
            "NULL,N'',N'',NULL,(SELECT COALESCE(MAX([order]),-1)+1 FROM dbo.positions WHERE system_id=@system)," +
            "1,N'',NULL,NULL,NULL,N'',N'',NULL,NULL)", conn, tx))
        {
            insert.Parameters.Add("@system", SqlDbType.BigInt).Value = systemId;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name.Trim();
            insert.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = unit.Trim();
            insert.Parameters.Add("@qty", SqlDbType.Float).Value = (double)qty;
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (purchaseCost is not null)
        {
            using var cost = new SqlCommand(
                "INSERT dbo.procurement (position_id,supplier_id,stage,order_ref,invoice_ref,cost_price," +
                "due_date,replacement,note,direct_to_site,source,updated_at,updated_by) " +
                "VALUES (@id,NULL,N'',N'',N'',@cost,NULL,N'',N'',0,N'buy',SYSDATETIME(),@user)", conn, tx);
            cost.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            cost.Parameters.Add("@cost", SqlDbType.Float).Value = (double)purchaseCost.Value;
            cost.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            cost.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (SYSDATETIME(),@user,N'position',@id,N'create',@details,@contract,@object)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = "вне КП: " + name.Trim();
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.ExecuteNonQuery();
        }
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new ExtraResult(true, id, null, next);
    }

    /// <summary>Change only an existing out-of-KP row, without changing client selling price.</summary>
    public static SqlServerDatabase.SaveResult EditExtra(
        string connectionString, int contractId, long positionId,
        string name, string unit, decimal qty, string expectedRevision)
    {
        var validation = ValidateExtra(name, unit, qty);
        if (validation is not null) return SqlServerDatabase.SaveResult.Fail(validation, expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите закупки.", rev);
        using (var cmd = new SqlCommand(
            "UPDATE p SET name=@name,unit=@unit,qty=@qty FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c WITH (HOLDLOCK) ON c.id=sy.contract_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE p.id=@id AND sy.contract_id=@contract AND p.out_of_kp=1 AND p.parent_id IS NULL AND c.locked=0 " +
            "AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0 " +
            "AND NOT EXISTS(SELECT 1 FROM dbo.act_lines a WHERE a.position_id=p.id) " +
            "AND NOT EXISTS(SELECT 1 FROM dbo.act_lines a JOIN dbo.positions ch ON ch.id=a.position_id WHERE ch.parent_id=p.id)", conn, tx))
        {
            cmd.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name.Trim();
            cmd.Parameters.Add("@unit", SqlDbType.NVarChar, 32).Value = unit.Trim();
            cmd.Parameters.Add("@qty", SqlDbType.Float).Value = (double)qty;
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (cmd.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Докупленная позиция не найдена, участвует в КС или документ заблокирован.", rev);
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (SYSDATETIME(),@user,N'position',@id,N'update',@details,@contract)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                "вне КП: " + name.Trim() + ", " + qty.ToString("0.######", CultureInfo.InvariantCulture);
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            log.ExecuteNonQuery();
        }
        var next = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, next);
    }

    /// <summary>Single-row compatibility entrypoint shares the strict atomic bulk checks.</summary>
    public static SqlServerDatabase.SaveResult DeleteExtra(
        string connectionString, int contractId, long positionId, string expectedRevision) =>
        DeleteExtras(connectionString, contractId, new[] { positionId }, expectedRevision);

    /// <summary>
    /// Python dop_from_extras: move all out-of-KP ROOT positions (and children) to a
    /// new supplementary agreement in ONE transaction. Position IDs, movements,
    /// procurement, shipment links and history are retained.
    /// </summary>
    public static ExtraResult CreateDopFromExtras(
        string connectionString, int sourceContractId, string expectedRevision, IReadOnlyList<long>? expectedRoots = null)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return ExtraResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return ExtraResult.Fail("Данные изменены. Обновите закупки.", rev);

        long objectId;
        long? companyId;
        decimal vat;
        string form;
        using (var source = new SqlCommand(
            "SELECT c.object_id,c.kind,c.company_id,c.vat_rate,ISNULL(c.form,N''),c.locked " +
            "FROM dbo.contracts c WITH (UPDLOCK,HOLDLOCK) JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "WHERE c.id=@contract AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0", conn, tx))
        {
            source.Parameters.Add("@contract", SqlDbType.BigInt).Value = sourceContractId;
            using var reader = source.ExecuteReader();
            if (!reader.Read()) return ExtraResult.Fail("Договор не найден.", rev);
            objectId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var kind = reader.GetString(1);
            if (kind is not ("main" or "kp"))
                return ExtraResult.Fail("Допсоглашение создаётся только из основного договора или КП.", rev);
            companyId = reader.IsDBNull(2) ? null :
                Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            vat = Convert.ToDecimal(reader.GetValue(3), CultureInfo.InvariantCulture);
            form = reader.GetString(4);
            if (Convert.ToBoolean(reader.GetValue(5), CultureInfo.InvariantCulture))
                return ExtraResult.Fail("Подписанный документ нельзя изменять: перенос отменён.", rev);
        }
        var roots = new List<(long Id, long SystemId, string System, long? CatalogId)>();
        using (var select = new SqlCommand(
            "SELECT p.id,p.system_id,sy.name,p.catalog_item_id " +
            "FROM dbo.positions p WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND p.out_of_kp=1 AND p.parent_id IS NULL " +
            "ORDER BY sy.[order],sy.id,p.[order],p.id", conn, tx))
        {
            select.Parameters.Add("@contract", SqlDbType.BigInt).Value = sourceContractId;
            using var reader = select.ExecuteReader();
            while (reader.Read())
                roots.Add((
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture)));
        }
        if (expectedRoots is not null && !roots.Select(root => root.Id).OrderBy(id => id)
            .SequenceEqual(expectedRoots.OrderBy(id => id)))
            return ExtraResult.Fail("Состав докупленного изменился. Обновите подтверждение.", rev);
        if (roots.Count == 0) return ExtraResult.Fail("Вне КП нет докупленных позиций для ДС.", rev);
        if (roots.Count > 2500) return ExtraResult.Fail("Слишком много дополнительных позиций.", rev);

        // Validate the complete supported hierarchy before creating the destination.
        // A cross-system child or deeper nesting must never move another document's rows.
        foreach (var root in roots)
        {
            using (var hierarchy = new SqlCommand(
                "SELECT TOP(1) ch.id FROM dbo.positions ch WITH (UPDLOCK,HOLDLOCK) " +
                "WHERE ch.parent_id=@id AND (ch.system_id<>@system OR " +
                "EXISTS(SELECT 1 FROM dbo.positions g WHERE g.parent_id=ch.id))", conn, tx))
            {
                hierarchy.Parameters.Add("@id", SqlDbType.BigInt).Value = root.Id;
                hierarchy.Parameters.Add("@system", SqlDbType.BigInt).Value = root.SystemId;
                if (hierarchy.ExecuteScalar() is not null)
                    return ExtraResult.Fail("Некорректная иерархия комплектующих; перенос отменён.", rev);
            }
            // Don't corrupt already drafted/signed KS documents by moving their base position.
            using var check = new SqlCommand(
                "SELECT TOP(1) al.position_id FROM dbo.act_lines al WHERE al.position_id=@id OR " +
                "al.position_id IN (SELECT id FROM dbo.positions WHERE parent_id=@id)", conn, tx);
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = root.Id;
            if (check.ExecuteScalar() is not null)
                return ExtraResult.Fail("Докупленная позиция участвует в КС-2; перенос отменён.", rev);
        }

        long dopId;
        using (var insert = new SqlCommand(
            @"INSERT dbo.contracts
              (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
               transport_amount,source_file,source_sheet,[order],created_at,
               company_id,base_contract_id,smr_mode,smr_percent,note,kp_phase,
               stage_rest,form,m2_mode,pnr_amount,transport_note,locked,locked_by)
              OUTPUT INSERTED.id
              VALUES (@obj,N'dop',N'докупленное',N'',N'',N'',@vat,0,
                      0,N'',N'',(SELECT COALESCE(MAX([order]),0)+1 FROM dbo.contracts WHERE object_id=@obj),
                      SYSDATETIME(),@company,@base,N'sum',0,N'',N'',0,@form,N'',0,N'',0,N'')", conn, tx))
        {
            insert.Parameters.Add("@obj", SqlDbType.BigInt).Value = objectId;
            insert.Parameters.Add("@vat", SqlDbType.Float).Value = (double)vat;
            insert.Parameters.Add("@company", SqlDbType.BigInt).Value =
                companyId is null ? DBNull.Value : companyId.Value;
            insert.Parameters.Add("@base", SqlDbType.BigInt).Value = sourceContractId;
            insert.Parameters.Add("@form", SqlDbType.NVarChar, 32).Value = form;
            dopId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            SqlServerAgreements.AttachNewDocument(conn, tx, dopId);
        }
        var destinations = new Dictionary<string, long>(StringComparer.Ordinal);
        var order = new Dictionary<long, int>();
        foreach (var extra in roots)
        {
            var name = extra.System == "Вне КП" ? "Дополнительно" : extra.System;
            if (!destinations.TryGetValue(name, out var destination))
            {
                using var add = new SqlCommand(
                    "INSERT dbo.systems " +
                    "(contract_id,name,[order],parent_id,smr_amount,heading,source_num,stage_sys_src) " +
                    "OUTPUT INSERTED.id VALUES (@contract,@name,@ord,NULL,NULL,N'',N'',NULL)", conn, tx);
                add.Parameters.Add("@contract", SqlDbType.BigInt).Value = dopId;
                add.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = name;
                add.Parameters.Add("@ord", SqlDbType.Int).Value = destinations.Count;
                destination = Convert.ToInt64(add.ExecuteScalar(), CultureInfo.InvariantCulture);
                destinations.Add(name, destination);
                order.Add(destination, 0);
            }
            decimal price = 0m;
            if (extra.CatalogId is long item)
            {
                using var catalog = new SqlCommand(
                    "SELECT TOP(1) price FROM dbo.price_history " +
                    "WHERE item_id=@item AND price>0 ORDER BY created_at DESC,id DESC", conn, tx);
                catalog.Parameters.Add("@item", SqlDbType.BigInt).Value = item;
                var found = catalog.ExecuteScalar();
                if (found is not null and not DBNull)
                    price = Convert.ToDecimal(found, CultureInfo.InvariantCulture);
            }
            if (price == 0m)
            {
                using var cost = new SqlCommand(
                    "SELECT COALESCE(pr.cost_price,p.purchase_price,0) " +
                    "FROM dbo.positions p LEFT JOIN dbo.procurement pr ON pr.position_id=p.id " +
                    "WHERE p.id=@id", conn, tx);
                cost.Parameters.Add("@id", SqlDbType.BigInt).Value = extra.Id;
                price = Convert.ToDecimal(cost.ExecuteScalar() ?? 0m, CultureInfo.InvariantCulture);
            }
            // Preserve child order and parent relationship. Do not recreate position IDs:
            // movements, supplier progress and shipment_lines remain attached.
            var nextOrder = order[destination];
            using (var relocate = new SqlCommand(
                "UPDATE dbo.positions SET system_id=@target,out_of_kp=0,price=@price,[order]=@ord " +
                "WHERE id=@id AND system_id=@source AND out_of_kp=1 AND parent_id IS NULL", conn, tx))
            {
                relocate.Parameters.Add("@target", SqlDbType.BigInt).Value = destination;
                relocate.Parameters.Add("@price", SqlDbType.Float).Value = (double)price;
                relocate.Parameters.Add("@ord", SqlDbType.Int).Value = nextOrder++;
                relocate.Parameters.Add("@id", SqlDbType.BigInt).Value = extra.Id;
                relocate.Parameters.Add("@source", SqlDbType.BigInt).Value = extra.SystemId;
                if (relocate.ExecuteNonQuery() != 1)
                    throw new DBConcurrencyException("Дополнительная позиция изменилась.");
            }
            using (var children = new SqlCommand(
                "SELECT id FROM dbo.positions WITH (UPDLOCK,HOLDLOCK) WHERE parent_id=@root AND system_id=@source ORDER BY [order],id", conn, tx))
            {
                children.Parameters.Add("@root", SqlDbType.BigInt).Value = extra.Id;
                children.Parameters.Add("@source", SqlDbType.BigInt).Value = extra.SystemId;
                var ids = new List<long>();
                using (var reader = children.ExecuteReader())
                    while (reader.Read())
                        ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
                foreach (var id in ids)
                {
                    using var move = new SqlCommand(
                        "UPDATE dbo.positions SET system_id=@destination,[order]=@ord " +
                        "WHERE id=@id AND parent_id=@parent AND system_id=@source", conn, tx);
                    move.Parameters.Add("@destination", SqlDbType.BigInt).Value = destination;
                    move.Parameters.Add("@ord", SqlDbType.Int).Value = nextOrder++;
                    move.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
                    move.Parameters.Add("@parent", SqlDbType.BigInt).Value = extra.Id;
                    move.Parameters.Add("@source", SqlDbType.BigInt).Value = extra.SystemId;
                    if (move.ExecuteNonQuery() != 1)
                        throw new DBConcurrencyException("Комплектующая изменилась.");
                }
            }
            order[destination] = nextOrder;
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (SYSDATETIME(),@user,N'contract',@id,N'create',@details,@id,@object)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = dopId;
            log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"ДС из докупленного: {roots.Count} позиций из договора {sourceContractId}";
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new ExtraResult(true, dopId, null, updated);
    }

}
