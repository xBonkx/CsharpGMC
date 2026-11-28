using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Варианты КП и этапы на SQL Server — перенос stages.py (базовое ядро): создание
/// этапа полного КП, установка количеств строк в этапе (с комплектующими и защитой
/// актов/выдач), чтение таблицы распределения. Пользовательский интерфейс режим
/// «этапы» и «Остаток» — следующий блок; до него команды недоступны из окна.
/// </summary>
public static partial class SqlServerStages
{
    private const double Eps = 1e-6;

    public sealed record StageInfo(long Id, int No, bool Rest, string Name, string Title, decimal? StageSum)
    {
        public bool Locked { get; init; }
    }

    /// <summary>Этапы полного КП; «Остаток» последним (stages.stages_of).</summary>
    public static List<StageInfo> StagesOf(SqlConnection conn, long fullId, bool rest = true,
        SqlTransaction? tx = null) =>
        ReadStagesOf(conn, fullId, rest, tx, CancellationToken.None);

    private static List<StageInfo> ReadStagesOf(SqlConnection conn, long fullId, bool rest,
        SqlTransaction? tx, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var list = new List<StageInfo>();
        using var cmd = new SqlCommand(
            "SELECT id,stage_no,stage_rest,name,stage_sum,locked FROM dbo.contracts " +
            "WHERE stage_of=@id ORDER BY stage_no,id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
        using var cancellation = SqlReadCancellation.Register(cmd, token);
        using var r = SqlReadCancellation.ExecuteReader(cmd, token);
        while (r.Read())
        {
            token.ThrowIfCancellationRequested();
            var rowNo = r.IsDBNull(1) ? 0 : SqlInteger.ReadInt32(r, 1);
            var isRest = !r.IsDBNull(2) && SqlFlag.Read(r, 2);
            var stageName = r.IsDBNull(3) ? "" : r.GetString(3);
            list.Add(new StageInfo(
                SqlInteger.ReadInt64(r, 0),
                rowNo, isRest, stageName,
                Documents.ContractTitleText("kp", "", "", stageName),
                r.IsDBNull(4) ? null : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)))
                {
                    Locked = !r.IsDBNull(5) && SqlFlag.Read(r, 5)
                });
        }
        token.ThrowIfCancellationRequested();
        var ordered = list.OrderBy(x => x.Rest).ToList();
        return rest ? ordered : ordered.Where(x => !x.Rest).ToList();
    }

    /// <summary>Полное КП этапа (stages.full_of): сам документ или его stage_of.</summary>
    public static long FullOf(SqlConnection conn, long contractId)
    {
        using var cmd = new SqlCommand(
            "SELECT COALESCE(stage_of, id) FROM dbo.contracts WHERE id=@id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        return SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? contractId);
    }

    public static async Task<long> FullOfAsync(SqlConnection conn, long contractId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cmd = new SqlCommand(
            "SELECT COALESCE(stage_of, id) FROM dbo.contracts WHERE id=@id", conn);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        var result = await cmd.ExecuteScalarAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return Convert.ToInt64(result ?? contractId, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Новый пустой этап полного КП (stages.create_stage): реквизиты и режим СМР из
    /// полного КП, суммы нулевые, номер — следующий; строки заходят установкой количеств.
    /// </summary>
    public static long CreateStage(string connectionString, long fullId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableFull(conn, tx, fullId);

        string objectName, fullName, fullTitle, number, date, form, smrMode;
        decimal vatRate;
        long? companyId;
        using (var read = new SqlCommand(
            "SELECT c.name, c.number, c.[date], c.form, c.smr_mode, c.smr_percent, c.company_id, " +
            "c.vat_rate, c.locked, c.stage_of, o.name " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new ArgumentException("Полное КП не найдено.");
            fullName = r.IsDBNull(0) ? "" : r.GetString(0);
            number = r.IsDBNull(1) ? "" : r.GetString(1);
            date = r.IsDBNull(2) ? "" : r.GetString(2);
            form = r.IsDBNull(3) ? "" : r.GetString(3);
            smrMode = r.IsDBNull(4) ? "" : r.GetString(4);
            companyId = r.IsDBNull(6) ? null : SqlInteger.ReadInt64(r, 6);
            vatRate = r.IsDBNull(7) ? 22m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture));
            if (!r.IsDBNull(9) && SqlInteger.ReadInt64(r, 9) != fullId)
                throw new ArgumentException("Это этап другого документа — этап создаётся у полного КП.");
            objectName = r.IsDBNull(10) ? "" : r.GetString(10);
            var locked = !r.IsDBNull(8) && SqlFlag.Read(r, 8);
            if (locked) throw new InvalidOperationException("Полное КП подписано — этапы создавать нельзя.");
            fullTitle = Documents.ContractTitleText("kp", number, date, fullName);
        }
        var smrPercent = 0m;
        using (var percent = new SqlCommand(
            "SELECT smr_percent FROM dbo.contracts WHERE id=@id", conn, tx))
        {
            percent.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            smrPercent = Money.FromDouble(Convert.ToDouble(
                percent.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture));
        }

        int nextNo;
        using (var no = new SqlCommand(
            "SELECT COALESCE(MAX(stage_no),0) FROM dbo.contracts WHERE stage_of=@id", conn, tx))
        {
            no.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            nextNo = SqlInteger.RequireInt32(no.ExecuteScalar()) + 1;
        }
        var stageName = fullName.Trim().Length > 0
            ? $"{fullName.Trim()} — Этап {nextNo}" : $"Этап {nextNo}";

        long stageId;
        using (var insert = new SqlCommand(
            @"INSERT dbo.contracts
              (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
               transport_amount,source_file,source_sheet,[order],created_at,
               company_id,base_contract_id,smr_mode,smr_percent,note,kp_phase,
               stage_rest,form,m2_mode,pnr_amount,transport_note,locked,locked_by,
               stage_of,stage_no)
              OUTPUT INSERTED.id
              SELECT c.object_id,N'kp',@name,N'',c.number,c.[date],c.vat_rate,0,
                     0,N'',N'',
                     (SELECT COALESCE(MAX([order]),0)+1 FROM dbo.contracts WHERE object_id=c.object_id),
                     SYSDATETIME(),c.company_id,NULL,c.smr_mode,c.smr_percent,N'',c.kp_phase,
                     0,c.form,c.m2_mode,0,N'',0,N'',c.id,@no
              FROM dbo.contracts c WHERE c.id=@full", conn, tx))
        {
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = stageName;
            insert.Parameters.Add("@no", SqlDbType.Int).Value = nextNo;
            insert.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            stageId = SqlInteger.RequireInt64(insert.ExecuteScalar());
            SqlServerAgreements.AttachNewDocument(conn, tx, stageId);
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'create',@details,@contract,NULL)", conn, tx))
        {
            log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
            log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                $"{objectName}: этап {nextNo} из «{fullTitle}»";
            log.Parameters.Add("@contract", SqlDbType.BigInt).Value = stageId;
            log.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return stageId;
    }

    /// <summary>
    /// Установить в этапе количество строки полного КП (stages.set_qty): 0 — убрать
    /// из этапа; комплектующие масштабируются, разделы этапа создаются по stage_sys_src,
    /// удаление — с защитой актов и выдач. Журнал position/edit на строке полного КП.
    /// </summary>
    public static SqlServerDatabase.SaveResult SetStageQty(string connectionString, long stageId,
        long srcPositionId, decimal qty, string expectedRevision,
        decimal? expectedStageQty = null, decimal? expectedRest = null, string? qtyFormula = null)
    {
        if (stageId <= 0 || srcPositionId <= 0 || qty < 0m || qty > NumberText.MaxNumber)
            return SqlServerDatabase.SaveResult.Fail("Недопустимый этап, позиция или количество.", expectedRevision);
        qty = decimal.Round(qty, 6);
        if (!string.IsNullOrEmpty(qtyFormula) &&
            (qty <= (decimal)Eps || NumberText.Parse(qtyFormula) is not decimal calculated ||
             decimal.Round(calculated, 6) != qty || NumberText.FormulaText(qtyFormula) != qtyFormula))
            return SqlServerDatabase.SaveResult.Fail("Формула этапа должна давать указанное положительное количество.", expectedRevision);
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Сначала обнови смету.", rev);

        // Never edit a signed stage, a computed rest, or an unrelated
        // contract. Verify the actual stage document under this transaction.
        long fullId;
        using (var stage = new SqlCommand(
            "SELECT stage_of, stage_rest, locked FROM dbo.contracts WITH (UPDLOCK,HOLDLOCK) WHERE id=@id",
            conn, tx))
        {
            stage.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            using var r = stage.ExecuteReader();
            if (!r.Read() || r.IsDBNull(0))
                return SqlServerDatabase.SaveResult.Fail("Выбранный документ не является этапом полного КП.", rev);
            fullId = SqlInteger.ReadInt64(r, 0);
            if (fullId <= 0 || (!r.IsDBNull(1) && SqlFlag.Read(r, 1)))
                return SqlServerDatabase.SaveResult.Fail("Остаток рассчитывается автоматически и не редактируется.", rev);
            if (!r.IsDBNull(2) && SqlFlag.Read(r, 2))
                return SqlServerDatabase.SaveResult.Fail("Подписанный этап заблокирован.", rev);
        }
        if (StageGuardProblem(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Quantity) is { } blocker)
            return SqlServerDatabase.SaveResult.Fail(blocker, rev);
        string stageTitle, srcName, srcSystemPath;
        var srcSystemId = 0L;
        decimal srcQty, oldQty = 0m;
        using (var read = new SqlCommand(
            "SELECT p.name, p.qty, sy.id, c.name FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id " +
            "WHERE p.id=@id AND sy.contract_id=@full AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = srcPositionId;
            read.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Строка полного КП не найдена.", rev);
            srcName = r.IsDBNull(0) ? "" : r.GetString(0);
            srcQty = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            srcSystemId = SqlInteger.ReadInt64(r, 2);
            stageTitle = r.IsDBNull(3) ? "" : r.GetString(3);
        }
        srcSystemPath = SystemPath(conn, tx, srcSystemId);

        // Существующая строка этапа по stage_src.
        long? currentId = null;
        using (var current = new SqlCommand(
            "SELECT qty FROM dbo.positions WHERE stage_src=@src AND parent_id IS NULL AND out_of_kp=0 " +
            "AND system_id IN (SELECT id FROM dbo.systems WHERE contract_id=@stage)", conn, tx))
        {
            current.Parameters.Add("@src", SqlDbType.BigInt).Value = srcPositionId;
            current.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            var value = current.ExecuteScalar();
            if (value is not null and not DBNull)
                oldQty = Money.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (value is not null and not DBNull) currentId = FindStageRow(conn, tx, stageId, srcPositionId);
        }

        if (expectedStageQty is decimal seen &&
            Math.Abs(oldQty - seen) > (decimal)Eps)
            return SqlServerDatabase.SaveResult.Fail(
                "Количество этапа изменено в другом окне. Обнови смету.", rev);
        if (expectedRest is decimal expectedRemaining)
        {
            // Match Python stage_mode.save's snapshot check in the SAME
            // serializable transaction, not against UI-cached values only.
            decimal assignedElsewhere = 0m;
            using (var allocations = new SqlCommand(
                "SELECT p.qty FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "JOIN dbo.contracts c ON c.id=sy.contract_id " +
                "WHERE c.stage_of=@full AND ISNULL(c.stage_rest,0)=0 " +
                "AND p.stage_src=@src AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx))
            {
                allocations.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
                allocations.Parameters.Add("@src", SqlDbType.BigInt).Value = srcPositionId;
                using var reader = allocations.ExecuteReader();
                while (reader.Read())
                    assignedElsewhere += reader.IsDBNull(0) ? 0m :
                        Money.FromDouble(Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture));
            }
            var actualRest = srcQty - assignedElsewhere;
            if (Math.Abs(actualRest - expectedRemaining) > (decimal)Eps)
                return SqlServerDatabase.SaveResult.Fail(
                    "Распределение по этапам изменилось. Сначала обнови смету.", rev);
        }

        if (currentId is long protectedRow)
            RequireUncommittedQuantity(conn, tx, protectedRow, qty);

        if (qty <= (decimal)Eps)
        {
            if (currentId is long rowId)
            {
                DropStageRow(conn, tx, rowId);
                DropEmptySystems(conn, tx, stageId);
            }
        }
        else if (currentId is null)
        {
            var stageSystemId = StageSystemFor(conn, tx, stageId, srcSystemId, srcSystemPath);
            currentId = CopyStageRow(conn, tx, stageId, stageSystemId, srcPositionId, qty, null);
            if (!string.IsNullOrEmpty(qtyFormula))
            {
                using var formula = new SqlCommand("UPDATE dbo.positions SET formulas=@f WHERE id=@id", conn, tx);
                formula.Parameters.Add("@f", SqlDbType.NVarChar, -1).Value = StageQtyFormulas(conn, tx, currentId.Value, qtyFormula);
                formula.Parameters.Add("@id", SqlDbType.BigInt).Value = currentId.Value;
                formula.ExecuteNonQuery();
            }
            SyncStageChildren(conn, tx, stageId, srcPositionId, currentId.Value,
                stageSystemId, srcQty, qty);
        }
        else
        {
            using var update = new SqlCommand(
                "UPDATE dbo.positions SET qty=@q, formulas=@formulas WHERE id=@id", conn, tx);
            update.Parameters.Add("@q", SqlDbType.Float).Value = (double)qty;
            update.Parameters.AddWithValue("@formulas", StageQtyFormulas(conn, tx, currentId.Value, qtyFormula));
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = currentId.Value;
            update.ExecuteNonQuery();
            var parentSystem = StageSystemFor(conn, tx, stageId, srcSystemId, srcSystemPath);
            SyncStageChildren(conn, tx, stageId, srcPositionId, currentId.Value,
                parentSystem, srcQty, qty);
        }

        if (Math.Abs(oldQty - qty) > (decimal)Eps)
        {
            var objectId = ObjectOfContract(conn, tx, fullId);
            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
                "VALUES (@ts,@user,N'position',@id,N'edit',@details,@contract,@object)", conn, tx))
            {
                log.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
                log.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
                log.Parameters.Add("@id", SqlDbType.BigInt).Value = srcPositionId;
                log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value =
                    $"{stageTitle}: {srcName[..Math.Min(60, srcName.Length)]} " +
                    $"{oldQty.ToString("0.######", CultureInfo.InvariantCulture)} → " +
                    $"{qty.ToString("0.######", CultureInfo.InvariantCulture)} (этапы)";
                log.Parameters.Add("@contract", SqlDbType.BigInt).Value = fullId;
                log.Parameters.Add("@object", SqlDbType.BigInt).Value = objectId;
                log.ExecuteNonQuery();
            }
        }
        // The full KP is never modified by allocating a stage. Both the
        // stage's money and any computed «Остаток» stay in this transaction.
        RefreshMoney(conn, tx, fullId, stageId);
        var restId = RestDoc(conn, tx, fullId);
        if (restId is long rest && rest != stageId)
            SyncRestCore(conn, tx, fullId, rest);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    // ---------------------------------------------------------------- внутреннее

    private static string RemoveQtyFormula(SqlConnection conn, SqlTransaction tx, long positionId) =>
        StageQtyFormulas(conn, tx, positionId, null);

    private static string StageQtyFormulas(SqlConnection conn, SqlTransaction tx, long positionId, string? formula)
    {
        using var read = new SqlCommand(
            "SELECT formulas FROM dbo.positions WHERE id=@id", conn, tx);
        read.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        var raw = Convert.ToString(read.ExecuteScalar(), CultureInfo.InvariantCulture);
        System.Text.Json.Nodes.JsonObject? node;
        try
        {
            node = string.IsNullOrWhiteSpace(raw) ? new() :
                System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject
                ?? throw new FormatException("Некорректный JSON формул.");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            throw new InvalidOperationException("Нельзя безопасно обновить формулы позиции: " + ex.Message);
        }
        if (string.IsNullOrEmpty(formula)) node.Remove("qty");
        else node["qty"] = formula;
        return node.ToJsonString();
    }

    private static string SystemPath(SqlConnection conn, SqlTransaction tx, long systemId)
    {
        using var cmd = new SqlCommand(
            "SELECT sy.name, sy.parent_id FROM dbo.systems sy WHERE sy.id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = systemId;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return "";
        var name = r.IsDBNull(0) ? "" : r.GetString(0);
        var parent = r.IsDBNull(1) ? (long?)null : SqlInteger.ReadInt64(r, 1);
        r.Close();
        return parent is null ? name : SystemPath(conn, tx, parent.Value) + " › " + name;
    }

    private static long? FindStageRow(SqlConnection conn, SqlTransaction tx, long stageId, long srcPositionId)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP(1) p.id FROM dbo.positions p " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE p.stage_src=@src AND p.parent_id IS NULL AND p.out_of_kp=0 AND sy.contract_id=@stage", conn, tx);
        cmd.Parameters.Add("@src", SqlDbType.BigInt).Value = srcPositionId;
        cmd.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static List<long> ChildrenOfStage(SqlConnection conn, SqlTransaction tx, long stageRowId)
    {
        var ids = new List<long>();
        using var cmd = new SqlCommand(
            "SELECT id FROM dbo.positions WHERE parent_id=@id ORDER BY [order],id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = stageRowId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            ids.Add(SqlInteger.ReadInt64(r, 0));
        return ids;
    }

    private static long ChildSrcOf(SqlConnection conn, SqlTransaction tx, long stageRowId)
    {
        using var cmd = new SqlCommand("SELECT stage_src FROM dbo.positions WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = stageRowId;
        return SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Масштабирование комплектующей: qty ребёнка × (новое кол-во / старое кол-во родителя).</summary>
    private static double ScaledChild(SqlConnection conn, SqlTransaction tx, long childSrcId,
        long srcPositionId, decimal srcQty, decimal newQty)
    {
        double childQty;
        using (var read = new SqlCommand(
            "SELECT qty FROM dbo.positions WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = childSrcId;
            var value = read.ExecuteScalar();
            childQty = value is null or DBNull ? 0d : Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        if (srcQty > (decimal)Eps)
            return Math.Round(childQty * (double)newQty / (double)srcQty, 3, MidpointRounding.AwayFromZero);
        return childQty;
    }

    /// <summary>Раздел этапа по источнику: создаются недостающие с привязкой stage_sys_src.</summary>
    private static long StageSystemFor(SqlConnection conn, SqlTransaction tx, long stageId,
        long srcSystemId, string srcPath)
    {
        using var existing = new SqlCommand(
            "SELECT id FROM dbo.systems WHERE contract_id=@stage AND stage_sys_src=@src", conn, tx);
        existing.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        existing.Parameters.Add("@src", SqlDbType.BigInt).Value = srcSystemId;
        var found = existing.ExecuteScalar();
        if (found is not null and not DBNull)
            return Convert.ToInt64(found, CultureInfo.InvariantCulture);

        long? parentStageId = null;
        long? srcParentId = null;
        using (var read = new SqlCommand("SELECT parent_id FROM dbo.systems WHERE id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = srcSystemId;
            var value = read.ExecuteScalar();
            if (value is not null and not DBNull) srcParentId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        if (srcParentId is long parent)
        {
            var parentPath = SystemPath(conn, tx, parent);
            parentStageId = StageSystemFor(conn, tx, stageId, parent, parentPath);
        }
        // Копия раздела: имя/порядок/заголовок от источника, привязка stage_sys_src.
        long newId;
        using (var insert = new SqlCommand(
            @"INSERT dbo.systems (contract_id,parent_id,name,[order],smr_amount,heading,source_num,stage_sys_src)
              OUTPUT INSERTED.id
              SELECT @stage,@parent,sy.name,sy.[order],NULL,sy.heading,N'',sy.id
              FROM dbo.systems sy WHERE sy.id=@src", conn, tx))
        {
            insert.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
            insert.Parameters.Add("@parent", SqlDbType.BigInt).Value =
                parentStageId is null ? DBNull.Value : parentStageId.Value;
            insert.Parameters.Add("@src", SqlDbType.BigInt).Value = srcSystemId;
            newId = SqlInteger.RequireInt64(insert.ExecuteScalar());
        }
        return newId;
    }

    private static long CopyStageRow(SqlConnection conn, SqlTransaction tx, long stageId,
        long stageSystemId, long srcPositionId, decimal qty, long? parentId)
    {
        using var insert = new SqlCommand(
            @"INSERT dbo.positions
              (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price,purchase_price,
               note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual,
               work_price,cost_note,cost_color,work_sell,work_cost)
              OUTPUT INSERTED.id
              SELECT @system,@parent,p.catalog_item_id,p.num,p.basis,p.name,p.unit,@qty,p.price,
                     p.purchase_price,p.note,p.extra_json,NULL,p.[order],0,p.formulas,p.id,
                     p.area_manual,p.work_price,p.cost_note,p.cost_color,p.work_sell,p.work_cost
              FROM dbo.positions p WHERE p.id=@src", conn, tx);
        insert.Parameters.Add("@system", SqlDbType.BigInt).Value = stageSystemId;
        insert.Parameters.Add("@parent", SqlDbType.BigInt).Value =
            parentId is null ? DBNull.Value : parentId.Value;
        insert.Parameters.Add("@qty", SqlDbType.Float).Value = (double)qty;
        insert.Parameters.Add("@src", SqlDbType.BigInt).Value = srcPositionId;
        var id = SqlInteger.RequireInt64(insert.ExecuteScalar());
        // The source KP may use qty="=2*3". A stage's assigned quantity is
        // independent and must never retain the source qty formula.
        using (var clear = new SqlCommand(
            "UPDATE dbo.positions SET formulas=@formulas WHERE id=@id", conn, tx))
        {
            clear.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                RemoveQtyFormula(conn, tx, id);
            clear.Parameters.Add("@id", SqlDbType.BigInt).Value = id;
            clear.ExecuteNonQuery();
        }
        return id;
    }

    /// <summary>
    /// Replicate the source KP components and scale their quantities exactly
    /// as Python stages._scaled. Children never become independent stage rows.
    /// The helper also repairs a previously missing component without
    /// rewriting already existing documents outside this transaction.
    /// </summary>
    private static void SyncStageChildren(SqlConnection conn, SqlTransaction tx,
        long stageId, long sourceParentId, long stageParentId, long stageSystemId,
        decimal sourceQty, decimal assignedQty)
    {
        var sourceChildren = new List<long>();
        using (var read = new SqlCommand(
            "SELECT id FROM dbo.positions WHERE parent_id=@parent AND out_of_kp=0 " +
            "ORDER BY [order],id", conn, tx))
        {
            read.Parameters.Add("@parent", SqlDbType.BigInt).Value = sourceParentId;
            using var r = read.ExecuteReader();
            while (r.Read()) sourceChildren.Add(SqlInteger.ReadInt64(r, 0));
        }

        var existing = new Dictionary<long, long>();
        using (var read = new SqlCommand(
            "SELECT id,stage_src FROM dbo.positions WHERE parent_id=@parent AND stage_src IS NOT NULL",
            conn, tx))
        {
            read.Parameters.Add("@parent", SqlDbType.BigInt).Value = stageParentId;
            using var r = read.ExecuteReader();
            while (r.Read())
                existing[SqlInteger.ReadInt64(r, 1)] =
                    SqlInteger.ReadInt64(r, 0);
        }

        foreach (var childSrc in sourceChildren)
        {
            var value = ScaledChild(conn, tx, childSrc, sourceParentId, sourceQty, assignedQty);
            if (existing.TryGetValue(childSrc, out var childStage))
            {
                using var update = new SqlCommand(
                    "UPDATE dbo.positions SET qty=@qty, formulas=@formulas WHERE id=@id", conn, tx);
                update.Parameters.Add("@qty", SqlDbType.Float).Value = value;
                update.Parameters.Add("@formulas", SqlDbType.NVarChar, -1).Value =
                    RemoveQtyFormula(conn, tx, childStage);
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = childStage;
                update.ExecuteNonQuery();
            }
            else
            {
                CopyStageRow(conn, tx, stageId, stageSystemId, childSrc,
                    Convert.ToDecimal(value, CultureInfo.InvariantCulture), stageParentId);
            }
        }
    }

    private static void DropStageRow(SqlConnection conn, SqlTransaction tx, long rowId)
    {
        // Защита как у удаления позиции: любая ссылка в КС-2, проведённая или закреплённая выдача.
        using (var check = new SqlCommand(
            "SELECT TOP(1) p.name,a.number FROM dbo.act_lines al " +
            "JOIN dbo.positions p ON p.id=al.position_id JOIN dbo.acts a ON a.id=al.act_id " +
            "WHERE al.position_id IN (SELECT id FROM dbo.positions WHERE id=@id OR parent_id=@id)", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = rowId;
            using var r = check.ExecuteReader();
            if (r.Read())
            {
                var name = r.IsDBNull(0) ? "" : r.GetString(0);
                var number = r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                throw new InvalidOperationException(
                    $"«{name[..Math.Min(60, name.Length)]}» есть в КС-2 № {number}. Сначала убери строку из акта.");
            }
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) m.id FROM dbo.movements m WHERE m.position_id IN " +
            "(SELECT id FROM dbo.positions WHERE id=@id OR parent_id=@id) AND m.shipment_id IS NOT NULL", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = rowId;
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException("Позиция связана с проведённой выдачей. Проверь ведомость выдачи.");
        }
        using (var check = new SqlCommand(
            "SELECT TOP(1) sl.id FROM dbo.shipment_lines sl JOIN dbo.shipments sh ON sh.id=sl.shipment_id " +
            "WHERE sl.position_id IN (SELECT id FROM dbo.positions WHERE id=@id OR parent_id=@id) AND sh.fixed=1", conn, tx))
        {
            check.Parameters.Add("@id", SqlDbType.BigInt).Value = rowId;
            if (check.ExecuteScalar() is not null)
                throw new InvalidOperationException("Позиция связана с закреплённой ведомостью выдачи.");
        }
        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
            using (var delete = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] AS t WHERE t.position_id IN " +
                "(SELECT id FROM dbo.positions WHERE id=@id OR parent_id=@id)", conn, tx))
            {
                delete.Parameters.AddWithValue("@id", rowId);
                delete.ExecuteNonQuery();
            }
        using (var deleteSubs = new SqlCommand(
            "DELETE FROM dbo.positions WHERE parent_id=@id", conn, tx))
        {
            deleteSubs.Parameters.AddWithValue("@id", rowId);
            deleteSubs.ExecuteNonQuery();
        }
        using (var deleteRow = new SqlCommand(
            "DELETE FROM dbo.positions WHERE id=@id", conn, tx))
        {
            deleteRow.Parameters.AddWithValue("@id", rowId);
            deleteRow.ExecuteNonQuery();
        }
    }

    /// <summary>Пустые разделы этапа (созданные под этап) убираются — два прохода, как в Python.</summary>
    private static void DropEmptySystems(SqlConnection conn, SqlTransaction tx, long stageId)
    {
        for (var pass = 0; pass < 2; pass++)
            using (var delete = new SqlCommand(
                "DELETE sy FROM dbo.systems sy WHERE sy.contract_id=@stage AND sy.stage_sys_src IS NOT NULL " +
                "AND NOT EXISTS (SELECT 1 FROM dbo.positions p WHERE p.system_id=sy.id) " +
                "AND NOT EXISTS (SELECT 1 FROM dbo.systems sub WHERE sub.parent_id=sy.id)", conn, tx))
            {
                delete.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
                delete.ExecuteNonQuery();
            }
    }

    // ---------------------------------------------------------------- таблица и остаток

    /// <summary>Строка таблицы распределения (stages.table): строка полного КП и количества по этапам.</summary>
    public sealed record StageTableRow(long SystemId, string SystemName, long SrcId, string Name,
        string Unit, decimal Qty, decimal Price, decimal[] PerStage, decimal Rest)
    {
        public IReadOnlyList<string> StageFormulas { get; init; } = Array.Empty<string>();
    }

    /// <summary>Таблица «строка × этапы»: количества в каждом этапе и остаток (stages.table).</summary>
    public static (List<StageInfo> Stages, List<StageTableRow> Rows) StageTable(
        SqlConnection conn, long fullId, SqlTransaction? tx = null)
    {
        var stages = StagesOf(conn, fullId, rest: false, tx: tx);
        return (stages, ReadStageTableRows(conn, fullId, stages, tx, batchRead: false));
    }

    /// <summary>Read-only distribution; caller verifies the revision before/after.</summary>
    public static (List<StageInfo> Stages, List<StageTableRow> Rows, StageInfo? Remaining) ReadStageDistribution(
        SqlConnection conn, long fullId) =>
        ReadStageDistribution(conn, fullId, CancellationToken.None);

    public static (List<StageInfo> Stages, List<StageTableRow> Rows, StageInfo? Remaining) ReadStageDistribution(
        SqlConnection conn, long fullId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var all = ReadStagesOf(conn, fullId, true, null, token);
        var stages = all.Where(stage => !stage.Rest).ToList();
        return (stages, ReadStageTableRows(conn, fullId, stages, null, batchRead: true, token: token),
            all.FirstOrDefault(stage => stage.Rest));
    }

    private static List<StageTableRow> ReadStageTableRows(SqlConnection conn, long fullId,
        List<StageInfo> stages, SqlTransaction? tx, bool batchRead, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var maps = stages.Select(_ => new Dictionary<long, decimal>()).ToList();
        var formulas = stages.ToDictionary(s => s.Id, _ => new Dictionary<long, string>());
        if (batchRead && stages.Count > 0)
        {
            var byId = stages.Select((stage, index) => (stage.Id, Index: index))
                .ToDictionary(x => x.Id, x => maps[x.Index]);
            using var read = new SqlCommand(
                "SELECT sy.contract_id, p.stage_src, p.qty, p.formulas FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id JOIN dbo.contracts c ON c.id=sy.contract_id " +
                "WHERE c.stage_of=@full AND ISNULL(c.stage_rest,0)=0 " +
                "AND p.stage_src IS NOT NULL AND p.parent_id IS NULL AND p.out_of_kp=0 " +
                "ORDER BY sy.contract_id,p.id", conn);
            read.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var stageId = SqlInteger.ReadInt64(r, 0);
                if (!byId.TryGetValue(stageId, out var map)) continue;
                var srcId = SqlInteger.ReadInt64(r, 1);
                var qty = r.IsDBNull(2) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture));
                // Multiple physical rows for one stage/source are ambiguous.
                // Do not publish a potentially different allocation from the write path.
                if (!map.TryAdd(srcId, qty))
                    throw new InvalidOperationException("Повторяющаяся исходная позиция в этапе. Проверьте распределение.");
                var formula = EstimateCellFormula.Read(r.IsDBNull(3) ? "" : r.GetString(3), "qty");
                if (formula.Length > 0) formulas[stageId][srcId] = formula;
            }
        }
        else for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var map = maps[index];
            using var read = new SqlCommand(
                "SELECT stage_src, qty, p.formulas FROM dbo.positions p " +
                "JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@stage AND p.stage_src IS NOT NULL AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx);
            read.Parameters.Add("@stage", SqlDbType.BigInt).Value = stage.Id;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var srcId = SqlInteger.ReadInt64(r, 0);
                map[srcId] = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
                var formula = EstimateCellFormula.Read(r.IsDBNull(2) ? "" : r.GetString(2), "qty");
                if (formula.Length > 0) formulas[stage.Id][srcId] = formula;
            }
        }
        var rows = new List<StageTableRow>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.name, p.unit, p.qty, p.price, sy.id, sy.name " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@full AND p.parent_id IS NULL AND p.out_of_kp=0 " +
            "ORDER BY sy.[order], sy.id, p.[order], p.id", conn, tx))
        {
            read.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
            using var cancellation = SqlReadCancellation.Register(read, token);
            using var r = SqlReadCancellation.ExecuteReader(read, token);
            while (r.Read())
            {
                token.ThrowIfCancellationRequested();
                var srcId = SqlInteger.ReadInt64(r, 0);
                var qty = r.IsDBNull(3) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture));
                var perStage = new decimal[stages.Count];
                var stageFormulas = new string[stages.Count];
                var taken = 0m;
                for (var i = 0; i < stages.Count; i++)
                {
                    perStage[i] = maps[i].GetValueOrDefault(srcId);
                    taken += perStage[i];
                    stageFormulas[i] = formulas[stages[i].Id].GetValueOrDefault(srcId, "");
                }
                rows.Add(new StageTableRow(
                    SqlInteger.ReadInt64(r, 5),
                    r.IsDBNull(6) ? "" : r.GetString(6),
                    srcId,
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    qty,
                    r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                    perStage, qty - taken)
                {
                    StageFormulas = stageFormulas
                });
            }
        }
        token.ThrowIfCancellationRequested();
        return rows;
    }

    /// <summary>«Остаток» как документ (stages.create_rest): создаётся при первом обращении,
    /// количества всегда «полное КП − этапы», строка заблокирована для ручного ввода.</summary>
    public static long CreateRest(string connectionString, long fullId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableFull(conn, tx, fullId);

        var restId = RestDoc(conn, tx, fullId);
        if (restId is null)
        {
            restId = CreateStageCore(conn, tx, fullId);
            var fullName = ContractName(conn, tx, fullId);
            var restName = fullName.Trim().Length > 0 ? $"{fullName.Trim()} — Остаток" : "Остаток";
            using (var update = new SqlCommand(
                "UPDATE dbo.contracts SET stage_rest=1, name=@name, locked=1, " +
                "locked_by=@by, locked_at=@at WHERE id=@id", conn, tx))
            {
                update.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = restName;
                update.Parameters.Add("@by", SqlDbType.NVarChar, 64).Value = "этапы — количества считаются сами";
                update.Parameters.Add("@at", SqlDbType.DateTime2).Value = DateTime.Now;
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = restId.Value;
                update.ExecuteNonQuery();
            }
        }
        SyncRestCore(conn, tx, fullId, restId.Value);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return restId.Value;
    }

    /// <summary>Привести «Остаток» в соответствие (stages.sync_rest): количества = полное − этапы,
    /// имена/единицы/цены — как в полном КП. Возвращает число изменений.</summary>
    public static int SyncRest(string connectionString, long fullId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableFull(conn, tx, fullId);
        var restId = RestDoc(conn, tx, fullId);
        var changed = 0;
        if (restId is not null) changed = SyncRestCore(conn, tx, fullId, restId.Value);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return changed;
    }

    /// <summary>
    /// Same signed-document/ownership guard as SetStageQty, but also used by
    /// bulk actions ("Весь остаток", "Очистить"). A stale dialog must never
    /// overwrite a signed or another KP's stage.
    /// </summary>
    private static string? StageWriteProblem(SqlConnection conn, SqlTransaction tx,
        long fullId, long stageId) =>
        StageGuardProblem(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Quantity);

    /// <summary>Весь остаток — в этап (stages.put_rest).</summary>
    public static (SqlServerDatabase.SaveResult Result, int Rows) PutRest(string connectionString,
        long fullId, long stageId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return (SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision), 0);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return (SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови окно этапов.", rev), 0);
        if (StageWriteProblem(conn, tx, fullId, stageId) is { } problem)
            return (SqlServerDatabase.SaveResult.Fail(problem, rev), 0);
        var n = PutRestCore(conn, tx, fullId, stageId);
        RefreshMoney(conn, tx, fullId, stageId);
        var rest = RestDoc(conn, tx, fullId);
        if (rest is long restId && restId != stageId) SyncRestCore(conn, tx, fullId, restId);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return (new SqlServerDatabase.SaveResult(true, null, updated), n);
    }

    /// <summary>Очистить этап (stages.clear): все количества в ноль, деньги пересчитываются.</summary>
    public static SqlServerDatabase.SaveResult ClearStage(string connectionString, long fullId,
        long stageId, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови окно этапов.", rev);
        if (StageWriteProblem(conn, tx, fullId, stageId) is { } problem)
            return SqlServerDatabase.SaveResult.Fail(problem, rev);
        var (_, rows) = StageTable(conn, fullId, tx);
        foreach (var row in rows)
        {
            var mine = StageRowId(conn, tx, stageId, row.SrcId);
            if (mine is not null) SetQtyCore(conn, tx, stageId, fullId, row, 0m, mine.Value);
        }
        RefreshMoney(conn, tx, fullId, stageId);
        var rest = RestDoc(conn, tx, fullId);
        if (rest is long restId && restId != stageId) SyncRestCore(conn, tx, fullId, restId);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>Переименовать этап (stages.rename): «база — текст».</summary>
    public static void RenameStage(string connectionString, long fullId, long stageId,
        string text, string expectedRevision)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        RequireEditableStage(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Rename);
        var baseName = ContractName(conn, tx, fullId).Trim();
        text = text.Trim();
        var stageName = baseName.Length > 0 && text.Length > 0
            ? $"{baseName} — {text}" : text.Length > 0 ? text : "Этап";
        using var update = new SqlCommand(
            "UPDATE dbo.contracts SET name=@name WHERE id=@id", conn, tx);
        update.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = stageName;
        update.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
        update.ExecuteNonQuery();
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    /// <summary>«Сумма по договору» этапа — для сверки; включает синхронизацию денег полного КП.</summary>
    public static void SetStageTarget(string connectionString, long stageId,
        decimal? sum, string expectedRevision, long? expectedFullId = null)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        if (sum is < 0m || sum > NumberText.MaxNumber)
            throw new ArgumentOutOfRangeException(nameof(sum), "Недопустимая сумма договора.");
        var fullId = ParentForStage(conn, tx, stageId);
        if (expectedFullId is long expected && expected != fullId)
            throw new DBConcurrencyException("Этап не принадлежит выбранному полному КП.");
        RequireEditableStage(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Target);
        using (var update = new SqlCommand(
            "UPDATE dbo.contracts SET stage_sum=@sum WHERE id=@id", conn, tx))
        {
            update.Parameters.Add("@sum", SqlDbType.Float).Value =
                sum is null ? DBNull.Value : (object)(double)sum.Value;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            update.ExecuteNonQuery();
        }
        SyncParentMoney(conn, tx, fullId);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    /// <summary>Удалить этап: копия в корзину, защита и перечитывание денег полного КП
    /// (delete_contract + sync_parent_money).</summary>
    public static void DeleteStage(string connectionString, long stageId, string expectedRevision,
        long? expectedFullId = null)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        SqlServerWorkspace.CheckRevision(conn, tx, expectedRevision);
        var fullId = ParentForStage(conn, tx, stageId);
        if (expectedFullId is long expected && expected != fullId)
            throw new DBConcurrencyException("Этап не принадлежит выбранному полному КП.");
        RequireEditableStage(conn, tx, fullId, stageId, StageMutationPolicy.Operation.Delete);
        RequireNoDependentDocuments(conn, tx, stageId);
        var info = DescribeContract(conn, tx, stageId);
        SqlServerTrash.KeepContract(conn, tx, stageId, allowStage: true);
        LogDelete(conn, tx, stageId, $"{info.ObjectName}: {info.Title}");
        DeleteStageRows(conn, tx, stageId);
        Execute(conn, tx, "DELETE FROM dbo.contracts WHERE id=@id", stageId);
        RefreshStageGroup(conn, tx, fullId);
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
    }

    internal static void RefreshStageGroup(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        if (RestDoc(conn, tx, fullId) is long restId)
            SyncRestCore(conn, tx, fullId, restId);
        SyncParentMoney(conn, tx, fullId);
    }

    private static int SyncRestCore(SqlConnection conn, SqlTransaction tx, long fullId, long restId)
    {
        RequireRestNotUsedInActs(conn, tx, restId);
        var changed = 0;
        var (_, rows) = StageTable(conn, fullId, tx);
        var sourceIds = rows.Select(row => row.SrcId).ToHashSet();
        var originalRestRows = StageRowsOf(conn, tx, restId);
        var restBySource = originalRestRows.ToDictionary(row => row.SrcId, row => row.RowId);
        foreach (var row in rows)
        {
            var want = Math.Max(0m, decimal.Round(row.Rest, 6));
            long? mine = restBySource.TryGetValue(row.SrcId, out var existing) ? existing : null;
            if (mine is null)
            {
                if (want > 0m)
                {
                    var stageSystemId = StageSystemFor(conn, tx, restId, row.SystemId, row.SystemName);
                    var restParent = CopyStageRow(conn, tx, restId, stageSystemId, row.SrcId, want, null);
                    SyncStageChildren(conn, tx, restId, row.SrcId, restParent, stageSystemId, row.Qty, want);
                    changed++;
                }
            }
            else if (want <= (decimal)Eps)
            {
                // Python set_qty(..., 0) removes the old rest row, unless
                // it is protected by a KS/issue reference (then rollback).
                DropStageRow(conn, tx, mine.Value);
                changed++;
            }
            else if (Math.Abs(CurrentQty(conn, tx, mine.Value) - want) > (decimal)Eps)
            {
                SetQtyCore(conn, tx, restId, fullId, row, want, mine.Value);
                changed++;
            }
            if (mine is long id && want > (decimal)Eps)
                changed += SyncSameFields(conn, tx, id, row.SrcId);
        }
        // Строки, удалённые из полного КП, уходят и из остатка.
        foreach (var (rowId, srcId) in originalRestRows)
        {
            if (!sourceIds.Contains(srcId))
            {
                DropStageRow(conn, tx, rowId);
                changed++;
            }
        }
        if (changed > 0) DropEmptySystems(conn, tx, restId);
        RefreshMoney(conn, tx, fullId, restId);
        return changed;
    }

    private static int SyncSameFields(SqlConnection conn, SqlTransaction tx, long stageRowId, long srcId)
    {
        var changed = 0;
        using (var update = new SqlCommand(
            "UPDATE dst SET dst.name=src.name, dst.unit=src.unit, dst.price=src.price, " +
            "dst.purchase_price=src.purchase_price, dst.note=src.note, dst.work_price=src.work_price, " +
            "dst.area_manual=src.area_manual, dst.num=src.num " +
            "FROM dbo.positions dst JOIN dbo.positions src ON src.id=@src " +
            "WHERE dst.id=@id AND (dst.name<>src.name OR dst.unit<>src.unit OR dst.price<>src.price " +
            "OR ISNULL(dst.purchase_price,0)<>ISNULL(src.purchase_price,0) OR dst.note<>src.note " +
            "OR ISNULL(dst.work_price,0)<>ISNULL(src.work_price,0) OR ISNULL(dst.area_manual,0)<>ISNULL(src.area_manual,0) " +
            "OR dst.num<>src.num)", conn, tx))
        {
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = stageRowId;
            update.Parameters.Add("@src", SqlDbType.BigInt).Value = srcId;
            changed = update.ExecuteNonQuery();
        }
        return changed;
    }

    private static int PutRestCore(SqlConnection conn, SqlTransaction tx, long fullId, long stageId)
    {
        var n = 0;
        var (_, rows) = StageTable(conn, fullId, tx);
        foreach (var row in rows)
        {
            if (row.Rest <= (decimal)Eps) continue;
            var mine = StageRowId(conn, tx, stageId, row.SrcId);
            var current = mine is long id ? CurrentQty(conn, tx, id) : 0m;
            SetQtyCore(conn, tx, stageId, fullId, row, current + row.Rest, mine);
            n++;
        }
        return n;
    }

    private static void SetQtyCore(SqlConnection conn, SqlTransaction tx, long stageId,
        long fullId, StageTableRow row, decimal qty, long? currentId)
    {
        if (currentId is long existingRow)
            RequireUncommittedQuantity(conn, tx, existingRow, qty);
        if (qty <= 0m)
        {
            if (currentId is long id) DropStageRow(conn, tx, id);
        }
        else if (currentId is null)
        {
            var stageSystemId = StageSystemFor(conn, tx, stageId, row.SystemId, row.SystemName);
            var stageParent = CopyStageRow(conn, tx, stageId, stageSystemId, row.SrcId, qty, null);
            SyncStageChildren(conn, tx, stageId, row.SrcId, stageParent, stageSystemId, row.Qty, qty);
        }
        else
        {
            using (var update = new SqlCommand(
                "UPDATE dbo.positions SET qty=@q, formulas=@formulas WHERE id=@id", conn, tx))
            {
                update.Parameters.Add("@q", SqlDbType.Float).Value = (double)qty;
                update.Parameters.AddWithValue("@formulas", RemoveQtyFormula(conn, tx, currentId.Value));
                update.Parameters.Add("@id", SqlDbType.BigInt).Value = currentId.Value;
                update.ExecuteNonQuery();
            }
            var stageSystem = StageSystemFor(conn, tx, stageId, row.SystemId, row.SystemName);
            SyncStageChildren(conn, tx, stageId, row.SrcId, currentId.Value, stageSystem,
                row.Qty, qty);
        }
    }

    private static decimal CurrentQty(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand("SELECT qty FROM dbo.positions WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = positionId;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? 0m : Money.FromDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture));
    }

    private static long? StageRowId(SqlConnection conn, SqlTransaction tx, long stageId, long srcId)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP(1) p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@stage AND p.stage_src=@src AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx);
        cmd.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        cmd.Parameters.Add("@src", SqlDbType.BigInt).Value = srcId;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static List<(long RowId, long SrcId)> StageRowsOf(SqlConnection conn, SqlTransaction tx, long stageId)
    {
        var list = new List<(long, long)>();
        using var cmd = new SqlCommand(
            "SELECT p.id, p.stage_src FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@stage AND p.stage_src IS NOT NULL AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx);
        cmd.Parameters.Add("@stage", SqlDbType.BigInt).Value = stageId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((SqlInteger.ReadInt64(r, 0),
                SqlInteger.ReadInt64(r, 1)));
        return list;
    }

    private static long? RestDoc(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.contracts WHERE stage_of=@id AND stage_rest=1", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string ContractName(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        using var cmd = new SqlCommand("SELECT name FROM dbo.contracts WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        return Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private sealed record ContractDeleteInfo(string Title, string ObjectName);

    private static ContractDeleteInfo DescribeContract(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        using var cmd = new SqlCommand(
            "SELECT c.kind,c.number,c.[date],c.name,o.name FROM dbo.contracts c " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return new ContractDeleteInfo("", "");
        string Text(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
        return new ContractDeleteInfo(
            Documents.ContractTitleText(Text(0), Text(1), Text(2), Text(3)), Text(4));
    }

    private static void LogDelete(SqlConnection conn, SqlTransaction tx, long contractId, string details)
    {
        using var cmd = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id,object_id) " +
            "VALUES (@ts,@user,N'contract',@id,N'delete',@details,@contract,NULL)", conn, tx);
        cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = DateTime.Now;
        cmd.Parameters.Add("@user", SqlDbType.NVarChar, 64).Value = Environment.UserName;
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = details;
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        cmd.ExecuteNonQuery();
    }

    private static void DeleteStageRows(SqlConnection conn, SqlTransaction tx, long stageId)
    {
        // Зависимые строки этапа: как в Python delete_sql_document_tree — строки актов
        // раньше позиций (FK NO ACTION), дети раньше родителей.
        using (var actLines = new SqlCommand(
            "DELETE al FROM dbo.act_lines al JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id=@id", conn, tx))
        {
            actLines.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            actLines.ExecuteNonQuery();
        }
        foreach (var table in new[] { "procurement", "movements", "shipment_lines" })
            using (var delete = new SqlCommand(
                $"DELETE t FROM dbo.[{table}] AS t JOIN dbo.positions p ON p.id=t.position_id " +
                "JOIN dbo.systems sy ON sy.id=p.system_id WHERE sy.contract_id=@id", conn, tx))
            {
                delete.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
                delete.ExecuteNonQuery();
            }
        using (var positions = new SqlCommand(
            "DELETE p FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@id", conn, tx))
        {
            positions.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            positions.ExecuteNonQuery();
        }
        using (var systems = new SqlCommand(
            "DELETE FROM dbo.systems WHERE contract_id=@id", conn, tx))
        {
            systems.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            systems.ExecuteNonQuery();
        }
        using (var actsHead = new SqlCommand(
            "DELETE FROM dbo.acts WHERE contract_id=@id", conn, tx))
        {
            actsHead.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            actsHead.ExecuteNonQuery();
        }
        using (var smr = new SqlCommand(
            "DELETE FROM dbo.smr_costs WHERE contract_id=@id", conn, tx))
        {
            smr.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            smr.ExecuteNonQuery();
        }
        using (var attachments = new SqlCommand(
            "DELETE FROM dbo.attachments WHERE contract_id=@id", conn, tx))
        {
            attachments.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            attachments.ExecuteNonQuery();
        }
        using (var specs = new SqlCommand(
            "DELETE FROM dbo.spec_revisions WHERE contract_id=@id", conn, tx))
        {
            specs.Parameters.Add("@id", SqlDbType.BigInt).Value = stageId;
            specs.ExecuteNonQuery();
        }
    }

    private static long CreateStageCore(SqlConnection conn, SqlTransaction tx, long fullId)
    {
        // Тот же INSERT, что и в CreateStage, без собственной транзакции.
        int nextNo;
        using (var no = new SqlCommand(
            "SELECT COALESCE(MAX(stage_no),0)+1 FROM dbo.contracts WHERE stage_of=@id", conn, tx))
        {
            no.Parameters.Add("@id", SqlDbType.BigInt).Value = fullId;
            nextNo = SqlInteger.RequireInt32(no.ExecuteScalar());
        }
        var fullName = ContractName(conn, tx, fullId).Trim();
        var stageName = fullName.Length > 0 ? $"{fullName} — Этап {nextNo}" : $"Этап {nextNo}";
        using var insert = new SqlCommand(
            @"INSERT dbo.contracts
              (object_id,kind,name,ks_site_part,number,[date],vat_rate,smr_amount,
               transport_amount,source_file,source_sheet,[order],created_at,
               company_id,base_contract_id,smr_mode,smr_percent,note,kp_phase,
               stage_rest,form,m2_mode,pnr_amount,transport_note,locked,locked_by,
               stage_of,stage_no)
              OUTPUT INSERTED.id
              SELECT c.object_id,N'kp',@name,N'',c.number,c.[date],c.vat_rate,0,
                     0,N'',N'',
                     (SELECT COALESCE(MAX([order]),0)+1 FROM dbo.contracts WHERE object_id=c.object_id),
                     SYSDATETIME(),c.company_id,NULL,c.smr_mode,c.smr_percent,N'',c.kp_phase,
                     0,c.form,c.m2_mode,0,N'',0,N'',c.id,@no
              FROM dbo.contracts c WHERE c.id=@full", conn, tx);
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 255).Value = stageName;
        insert.Parameters.Add("@no", SqlDbType.Int).Value = nextNo;
        insert.Parameters.Add("@full", SqlDbType.BigInt).Value = fullId;
        var created = SqlInteger.RequireInt64(insert.ExecuteScalar());
        SqlServerAgreements.AttachNewDocument(conn, tx, created);
        return created;
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static long ObjectOfContract(SqlConnection conn, SqlTransaction tx, long contractId)
    {
        using var cmd = new SqlCommand("SELECT object_id FROM dbo.contracts WHERE id=@id", conn, tx);
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        return SqlInteger.RequireInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
