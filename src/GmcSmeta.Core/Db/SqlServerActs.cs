using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// КС-2 (acts.py, базовое ядро без формул объёмов): создание/удаление актов,
/// количества закрытых позиций с защитой остатков (closed_by_acts), строки «вне
/// сметы», подпись акта. Формулы объёмов «К2» — отдельный блок.
/// </summary>
public static partial class SqlServerActs
{
    private const decimal Eps = 0.000001m;

    public sealed record ActInfo(long Id, string Number, DateTime Date,
        DateTime? PeriodFrom, DateTime? PeriodTo, bool Signed, int Lines, decimal Total,
        long ContractId = 0, string SourceName = "", decimal Ks3Net = 0m,
        decimal Vat = 0m, decimal Materials = 0m, decimal Smr = 0m,
        decimal Transport = 0m, decimal Pnr = 0m, decimal VatRate = 0m,
        bool VatOnTop = false, long GrossAdjustmentCents = 0,
        bool RowsExact = false, bool RoundRows = false,
        string DsNo = "", string DsDate = "",
        decimal DiscountPercent = 0m, bool DiscountOverride = false, long ManualTotalCents = -1,
        string SectionAmountsJson = "", bool SmrBySystem = false);

    /// <summary>
    /// Acts of a document with FULL gross totals matching Python Act.total:
    /// material lines, SMR, transport, PNR, VAT-on-top and adjustment.
    /// Uses three sequential readers, including inside an existing transaction.
    /// No per-act round trips and no nested DataReaders.
    /// </summary>
    public static List<ActInfo> ActsOf(SqlConnection conn, long contractId, SqlTransaction? transaction = null)
    {
        var ruleJson = new Dictionary<string, string>(StringComparer.Ordinal);
        using (var rules = new SqlCommand("SELECT [key],rules FROM dbo.tags", conn, transaction))
        using (var reader = rules.ExecuteReader())
            while (reader.Read())
                if (!reader.IsDBNull(0))
                    ruleJson[reader.GetString(0)] = reader.IsDBNull(1) ? "" : reader.GetString(1);

        var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
        var raw = new List<(long Id, string Number, DateTime Date, DateTime? Start, DateTime? End,
            bool Signed, decimal Smr, decimal Transport, decimal Pnr, bool Exact,
            long Adjustment, decimal VatRate, string Form, string DsNo, string DsDate,
            decimal Discount, bool Override, long ManualTotal, string SectionJson, bool BySystem)>();
        using (var cmd = new SqlCommand(
            "SELECT a.id,a.number,a.[date],a.period_from,a.period_to,a.signed," +
            "ISNULL(a.smr_amount,0),ISNULL(a.transport_amount,0),ISNULL(a.pnr_amount,0)," +
            "ISNULL(a.rows_exact,0),ISNULL(a.gross_adjustment_cents,0)," +
            "ISNULL(c.vat_rate,0),ISNULL(c.form,N''),ISNULL(a.ds_no,N''),ISNULL(a.ds_date,N'')" +
            (schema31 ? ",a.discount_percent,a.discount_override,a.manual_total_cents,a.section_amounts_json" : "") +
            ",a.smr_by_system " +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id " +
            "WHERE a.contract_id=@id ORDER BY a.[date],a.id", conn, transaction))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = cmd.ExecuteReader();
            decimal D(int k) => r.IsDBNull(k) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(k), CultureInfo.InvariantCulture));
            while (r.Read())
                raw.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture) ?? "",
                    r.IsDBNull(2) ? DateTime.Today : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture),
                    r.IsDBNull(3) ? null : Convert.ToDateTime(r.GetValue(3), CultureInfo.InvariantCulture),
                    r.IsDBNull(4) ? null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture),
                    !r.IsDBNull(5) && Convert.ToBoolean(r.GetValue(5), CultureInfo.InvariantCulture),
                    D(6), D(7), D(8),
                    !r.IsDBNull(9) && Convert.ToBoolean(r.GetValue(9), CultureInfo.InvariantCulture),
                    r.IsDBNull(10) ? 0L : Convert.ToInt64(r.GetValue(10), CultureInfo.InvariantCulture),
                    D(11), r.IsDBNull(12) ? "" : r.GetString(12),
                    r.IsDBNull(13) ? "" : r.GetString(13), r.IsDBNull(14) ? "" : r.GetString(14),
                    schema31 ? DiscountPolicy.ReadPercent(r, 15) : 0m,
                    schema31 && !r.IsDBNull(16) && Convert.ToBoolean(r.GetValue(16)),
                    schema31 && !r.IsDBNull(17) ? Convert.ToInt64(r.GetValue(17)) : -1L,
                    schema31 && !r.IsDBNull(18) ? r.GetString(18) : "",
                    !r.IsDBNull(schema31?19:15)&&Convert.ToBoolean(r.GetValue(schema31?19:15),CultureInfo.InvariantCulture)));
        }

        var lines = new Dictionary<long, List<(decimal Quantity, decimal Price)>>();
        using (var command = new SqlCommand(
            "SELECT al.act_id,al.qty,al.price FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id=@id", conn, transaction))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var qty = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
                var price = r.IsDBNull(2) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture));
                if (!lines.TryGetValue(id, out var list))
                    lines[id] = list = new List<(decimal Quantity, decimal Price)>();
                list.Add((qty, price));
            }
        }

        var acts = new List<ActInfo>(raw.Count);
        foreach (var a in raw)
        {
            var values = lines.GetValueOrDefault(a.Id) ?? new List<(decimal Quantity, decimal Price)>();
            var json = ruleJson.GetValueOrDefault(a.Form);
            var vatTop = KsFinancialPolicy.HasRule(json, "vat_top") || a.Form == "dns";
            var rowsExact = a.Exact;
            var mat = KsFinancialPolicy.Materials(values, rowsExact,
                KsFinancialPolicy.HasRule(json, "round_rows"));
            var sum = KsFinancialPolicy.Calculate(mat, a.Smr, a.Transport, a.Pnr,
                a.VatRate, vatTop, a.Adjustment, a.Discount, a.ManualTotal);
            acts.Add(new ActInfo(a.Id, a.Number, a.Date, a.Start, a.End,
                a.Signed, values.Count, sum.Gross,
                contractId, "", sum.Ks3Net, sum.Vat, sum.Materials,
                sum.Smr, sum.Transport, sum.Pnr, a.VatRate, vatTop, a.Adjustment,
                a.Exact, KsFinancialPolicy.HasRule(json, "round_rows"),
                a.DsNo, a.DsDate, a.Discount, a.Override, a.ManualTotal, a.SectionJson, a.BySystem));
        }
        return acts;
    }

    /// <summary>
    /// Read-only per-position progress across acts of one document. Uses two
    /// SQL Server reads (positions and their act quantities) instead of a
    /// query per row. One top-level position appears exactly once.
    /// </summary>
    public sealed record ProgressRow(long PositionId, string SystemName, string Name,
        string Unit, decimal QuantityByKp, decimal Price, decimal Closed,
        decimal SignedClosed, decimal Issued, bool OutsideKp,
        IReadOnlyDictionary<long, decimal> PerAct)
    {
        public decimal Remaining => Math.Max(0m, QuantityByKp - Closed);
        public decimal RemainingAmount => decimal.Round(Remaining * Price, 2);
        public decimal ClosedAmount => decimal.Round(Closed * Price, 2);
        public bool FullyClosed => Remaining <= 0.000001m;
    }

    public static IReadOnlyList<ProgressRow> ProgressForContract(
        string connectionString, long contractId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        var source = new List<(long Id, string System, string Name, string Unit,
            decimal Qty, decimal Price, decimal Done, decimal Signed, decimal Issued, bool Outside)>();

        using (var command = new SqlCommand(
            "SELECT p.id, s.name, p.name, p.unit, p.qty, p.price, " +
            "ISNULL(done.qty,0), ISNULL(done.signed_qty,0), ISNULL(moves.issued,0), p.out_of_kp " +
            "FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "OUTER APPLY (SELECT SUM(al.qty) qty, " +
            "SUM(CASE WHEN a.signed=1 THEN al.qty ELSE 0 END) signed_qty " +
            "FROM dbo.act_lines al JOIN dbo.acts a ON a.id=al.act_id " +
            "WHERE al.position_id=p.id AND a.contract_id=@id) done " +
            "OUTER APPLY (SELECT SUM(CASE WHEN m.kind=N'out' THEN m.qty " +
            "WHEN m.kind=N'back' THEN -m.qty ELSE 0 END) issued " +
            "FROM dbo.movements m WHERE m.position_id=p.id) moves " +
            "WHERE s.contract_id=@id AND p.parent_id IS NULL AND ISNULL(p.out_of_kp,0)=0 " +
            "ORDER BY s.[order],s.id,p.[order],p.id", conn))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = command.ExecuteReader();
            decimal D(int i) => r.IsDBNull(i) ? 0m :
                Money.FromDouble(Convert.ToDouble(r.GetValue(i), CultureInfo.InvariantCulture));
            string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
            while (r.Read())
                source.Add((
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    S(1), S(2), S(3), D(4), D(5), D(6), D(7), D(8),
                    !r.IsDBNull(9) && Convert.ToBoolean(r.GetValue(9), CultureInfo.InvariantCulture)));
        }

        var values = new Dictionary<long, Dictionary<long, decimal>>();
        using (var command = new SqlCommand(
            "SELECT al.position_id, al.act_id, SUM(al.qty) " +
            "FROM dbo.act_lines al JOIN dbo.acts a ON a.id=al.act_id " +
            "WHERE a.contract_id=@id GROUP BY al.position_id, al.act_id", conn))
        {
            command.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
            using var r = command.ExecuteReader();
            while (r.Read())
            {
                var positionId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                var actId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                var qty = r.IsDBNull(2) ? 0m :
                    Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture));
                if (!values.TryGetValue(positionId, out var dictionary))
                    values[positionId] = dictionary = new Dictionary<long, decimal>();
                dictionary[actId] = qty;
            }
        }

        return source.Select(r => new ProgressRow(
            r.Id, r.System, r.Name, r.Unit, r.Qty, r.Price, r.Done, r.Signed,
            r.Issued, r.Outside, values.GetValueOrDefault(r.Id)
                ?? new Dictionary<long, decimal>())).ToList();
    }

    public sealed record ActLineRow(long LineId, long PositionId, string Name, string Unit,
        decimal Qty, decimal Price, decimal WorkPrice, decimal QtyByKp, decimal ClosedByOthers,
        bool OutOfKp, bool Work);

    /// <summary>Строки акта + позиции сметы с остатками (для окна КС-2).</summary>
    public static (List<ActLineRow> Lines, List<ActLineRow> Available) ActRows(
        SqlConnection conn, long actId, long contractId)
    {
        var lines = new List<ActLineRow>();
        var closedByOthers = ClosedByActs(conn, contractId, actId);
        var actPositionIds = new HashSet<long>();
        using (var read = new SqlCommand(
            "SELECT al.id, al.position_id, p.name, p.unit, al.qty, al.price, al.work_price, p.qty, p.out_of_kp " +
            "FROM dbo.act_lines al JOIN dbo.positions p ON p.id=al.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE al.act_id=@id AND sy.contract_id=@contract " +
            "ORDER BY sy.[order],sy.id,p.[order],p.id", conn))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            read.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var positionId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
                actPositionIds.Add(positionId);
                var outOfKp = !r.IsDBNull(8) && Convert.ToBoolean(r.GetValue(8), CultureInfo.InvariantCulture);
                lines.Add(new ActLineRow(
                    Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                    positionId,
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "" : r.GetString(3),
                    Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                    r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                    r.IsDBNull(6) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(6), CultureInfo.InvariantCulture)),
                    r.IsDBNull(7) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(7), CultureInfo.InvariantCulture)),
                    closedByOthers.GetValueOrDefault(positionId),
                    outOfKp, EstimateStatistics.IsWorkName(r.IsDBNull(2) ? "" : r.GetString(2))));
            }
        }
        // Доступные позиции: верхние строки сметы (включая вне КП), ещё не в акте.
        var available = new List<ActLineRow>();
        using (var read = new SqlCommand(
            "SELECT p.id, p.name, p.unit, p.qty, p.price, p.work_price, p.out_of_kp " +
            "FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL " +
            "ORDER BY sy.[order], sy.id, p.[order], p.id", conn))
        {
            read.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            using var r = read.ExecuteReader();
            while (r.Read())
            {
                var positionId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                if (actPositionIds.Contains(positionId)) continue;
                var outOfKp = !r.IsDBNull(6) && Convert.ToBoolean(r.GetValue(6), CultureInfo.InvariantCulture);
                if (outOfKp) continue; // Python hides other acts' extra positions
                available.Add(new ActLineRow(
                    0, positionId,
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    0m,
                    r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture)),
                    r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture)),
                    r.IsDBNull(3) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(3), CultureInfo.InvariantCulture)),
                    closedByOthers.GetValueOrDefault(positionId),
                    outOfKp, EstimateStatistics.IsWorkName(r.IsDBNull(1) ? "" : r.GetString(1))));
            }
        }
        return (lines, available);
    }

    /// <summary>Закрыто другими актами (acts.closed_by_acts): {позиция: Σ количеств}.</summary>
    public static Dictionary<long, decimal> ClosedByActs(SqlConnection conn, long contractId, long excludeActId,
        SqlTransaction? transaction = null)
    {
        var map = new Dictionary<long, decimal>();
        using var cmd = new SqlCommand(
            "SELECT al.position_id, al.qty FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id WHERE a.contract_id=@cid AND a.id != @exclude", conn, transaction);
        cmd.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
        cmd.Parameters.Add("@exclude", SqlDbType.BigInt).Value = excludeActId;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var pid = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            var qty = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            map[pid] = map.GetValueOrDefault(pid) + qty;
        }
        return map;
    }

    /// <summary>Новый акт (acts.create_act): номер следующий, период от прошлого акта или
    /// works_start объекта; ДС-реквизиты наследуются; журнал act/create.</summary>
    public static long Create(string connectionString, long contractId, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        return CreateCore(conn, contractId, expectedRevision, SqlSchemaCompatibility.HasPreviewColumns(conn));
    }

    public static long CreateSchema31Test(string connectionString, long contractId, string expectedRevision)
    {
        using var conn = OpenSchema31WriteTest(connectionString);
        return CreateCore(conn, contractId, expectedRevision, true);
    }

    private static long CreateCore(SqlConnection conn, long contractId, string expectedRevision, bool schema31)
    {
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменены другим сотрудником. Обнови КС.");

        string objectName, contractTitle, kind, form;
        string worksStart = "";
        bool locked;
        using (var read = new SqlCommand(
            "SELECT c.locked, c.kind, c.form, o.name, o.works_start " +
            "FROM dbo.contracts c JOIN dbo.objects o ON o.id=c.object_id WHERE c.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", contractId);
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Документ не найден.");
            locked = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            kind = r.GetString(1);
            form = r.IsDBNull(2) ? "" : r.GetString(2);
            var kindTitle = "КС-2";
            objectName = r.IsDBNull(3) ? "" : r.GetString(3);
            worksStart = r.IsDBNull(4) ? "" : r.GetString(4);
        }
        if (locked) throw new InvalidOperationException("Смета подписана — новый акт нельзя создать.");

        var acts = ActsOf(conn, contractId, tx);
        var nextNumber = KsActNumbering.NextNumber(acts, acts, false);
        if (kind is "main" or "dop")
        {
            // Take all legal-agreement documents under the same SERIALIZABLE transaction;
            // another draft KS-2 can join the last unfinished KS-3 instead of
            // incorrectly starting an independent number sequence for the DS.
            var docs = new List<SqlServerKsContractScope.Document>();
            using (var read = new SqlCommand(
                "SELECT id,object_id,kind,number,name,base_contract_id " +
                "FROM dbo.contracts WHERE object_id=(SELECT object_id FROM dbo.contracts WHERE id=@id) " +
                "AND kind IN (N'main',N'dop')", conn, tx))
            {
                read.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
                using var r = read.ExecuteReader();
                while (r.Read())
                {
                    string S(int i) => r.IsDBNull(i) ? "" :
                        Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture) ?? "";
                    docs.Add(new SqlServerKsContractScope.Document(
                        Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture),
                        Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture),
                        S(2),S(3),S(4),
                        r.IsDBNull(5) ? null : Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture),
                        "",0m,""));
                }
            }
            var agreementDocs = SqlServerKsContractScope.ResolveDocuments(contractId, docs);
            if (agreementDocs.Count>1)
            {
                var across = new List<ActInfo>();
                var peerHasActs = false;
                foreach (var doc in agreementDocs)
                {
                    var documentActs = doc.Id==contractId ? acts : ActsOf(conn, doc.Id, tx);
                    across.AddRange(documentActs);
                    if (doc.Id!=contractId && documentActs.Count>0) peerHasActs=true;
                }
                nextNumber=KsActNumbering.NextNumber(acts,across,peerHasActs);
            }
        }
        var previous = acts.LastOrDefault();
        DateTime today = DateTime.Today;
        DateTime? start = previous?.PeriodTo;
        if (start is null && DateTime.TryParseExact(worksStart, "d.M.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            start = parsed;
        if (start is not null && start > today) start = today;

        long actId;
        using (var insert = new SqlCommand(
            "INSERT dbo.acts (contract_id,number,[date],period_from,period_to,signed,ds_no,ds_date,ds_extra," +
            "smr_amount,rows_exact,gross_adjustment_cents,note" +
            (schema31 ? ",discount_percent,discount_override,manual_total_cents,section_amounts_json" : "") + ") " +
            "OUTPUT INSERTED.id " +
            "SELECT id,@number,@date,@from," + (schema31 ? "@date" : "@from") +
            ",0,ISNULL(ds_no,N''),ISNULL(ds_date,NULL),ISNULL(ds_extra,N'')," +
            "0,0,0,N''" + (schema31 ? ",discount_percent,CONVERT(bit,0),-1,N''" : "") +
            " FROM dbo.contracts WHERE id=@cid", conn, tx))
        {
            insert.Parameters.AddWithValue("@number", nextNumber);
            insert.Parameters.AddWithValue("@date", today);
            insert.Parameters.AddWithValue("@from", start ?? today);
            insert.Parameters.AddWithValue("@cid", contractId);
            actId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        if (previous is not null)
        {
            // Python copies DNS/DS header details from the previous KS-2 of this document.
            using var copy = new SqlCommand(
                "UPDATE a SET ds_no=ISNULL(prev.ds_no,N''),ds_date=prev.ds_date," +
                "ds_extra=ISNULL(prev.ds_extra,N'') " +
                "FROM dbo.acts a JOIN dbo.acts prev ON prev.id=@previous " +
                "WHERE a.id=@id AND a.contract_id=prev.contract_id", conn, tx);
            copy.Parameters.Add("@previous", SqlDbType.BigInt).Value=previous.Id;
            copy.Parameters.Add("@id", SqlDbType.BigInt).Value=actId;
            copy.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'act',@id,N'create',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", actId);
            log.Parameters.AddWithValue("@details",
                $"{objectName} / {Documents.ContractTitleText(kind, "", "", "")}: КС-2 № {nextNumber}");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return actId;
    }

    /// <summary>Удалить акт (acts.delete_act): подписанный нельзя.</summary>
    public static SqlServerDatabase.SaveResult Delete(string connectionString, long actId,
        string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови КС.", rev);
        bool signed, locked;
        long contractId;
        string number;
        using (var read = new SqlCommand(
            "SELECT a.signed,c.locked,a.contract_id,ISNULL(a.number,N'') " +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id", conn, tx))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            signed=Convert.ToBoolean(r.GetValue(0),CultureInfo.InvariantCulture);
            locked=Convert.ToBoolean(r.GetValue(1),CultureInfo.InvariantCulture);
            contractId=Convert.ToInt64(r.GetValue(2),CultureInfo.InvariantCulture);
            number=r.GetString(3);
        }
        if (signed || locked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор удалять нельзя.", rev);
        using (var log=new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'act',@id,N'delete',@details,@contract)",conn,tx))
        {
            log.Parameters.AddWithValue("@ts",DateTime.Now);
            log.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id",actId);
            log.Parameters.AddWithValue("@details","КС-2 № "+number+": удаление черновика");
            log.Parameters.AddWithValue("@contract",contractId);
            log.ExecuteNonQuery();
        }
        var extras = new List<long>();
        using (var candidates = new SqlCommand(
            "SELECT DISTINCT al.position_id FROM dbo.act_lines al " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "WHERE al.act_id=@id AND p.out_of_kp=1", conn, tx))
        {
            candidates.Parameters.Add("@id", SqlDbType.BigInt).Value=actId;
            using var reader=candidates.ExecuteReader();
            while(reader.Read()) extras.Add(Convert.ToInt64(reader.GetValue(0),CultureInfo.InvariantCulture));
        }
        Execute(conn, tx, "DELETE FROM dbo.act_lines WHERE act_id=@id", actId);
        foreach (var extra in extras) CleanupOrphanExtra(conn,tx,extra);
        Execute(conn, tx, "DELETE FROM dbo.acts WHERE id=@id", actId);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Количество позиции в акте (acts.set_qty/_write_qty): 0 — убрать строку; сверх
    /// остатка нельзя (вне КП можно — количество строки следует за суммой актов);
    /// подписанный акт нельзя; журнал act/line; СМР «по поз.» пересчитывается.
    /// </summary>
    public static SqlServerDatabase.SaveResult SetQty(string connectionString, long actId,
        long positionId, decimal qty, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови КС.", rev);

        bool signed, contractLocked;
        long contractId;
        using (var read = new SqlCommand(
            "SELECT a.signed, a.contract_id, c.locked FROM dbo.acts a " +
            "JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", actId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            signed = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            contractId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            contractLocked = Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture);
        }
        if (signed || contractLocked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор изменять нельзя.", rev);

        if (!PositionBelongsToContract(conn, tx, positionId, contractId))
            return SqlServerDatabase.SaveResult.Fail("Позиция не принадлежит этому договору.", rev);

        decimal qtyByKp = 0m;
        string name = "", unit = "";
        bool outOfKp;
        decimal actPrice = 0m, actWorkPrice = 0m;
        using (var read = new SqlCommand(
            "SELECT p.qty, p.name, p.unit, p.out_of_kp, p.price, p.work_price " +
            "FROM dbo.positions p WHERE p.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", positionId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Позиция не найдена.", rev);
            qtyByKp = r.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
            name = r.IsDBNull(1) ? "" : r.GetString(1);
            unit = r.IsDBNull(2) ? "" : r.GetString(2);
            outOfKp = !r.IsDBNull(3) && Convert.ToBoolean(r.GetValue(3), CultureInfo.InvariantCulture);
            actPrice = r.IsDBNull(4) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(4), CultureInfo.InvariantCulture));
            actWorkPrice = r.IsDBNull(5) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(5), CultureInfo.InvariantCulture));
        }

        var closedByOthers = ClosedByActs(conn, contractId, actId, tx).GetValueOrDefault(positionId);
        long? lineId = null;
        decimal lineQty = 0m;
        using (var read = new SqlCommand(
            "SELECT TOP(1) id, qty FROM dbo.act_lines WHERE act_id=@act AND position_id=@pos", conn, tx))
        {
            read.Parameters.AddWithValue("@act", actId);
            read.Parameters.AddWithValue("@pos", positionId);
            using var r = read.ExecuteReader();
            if (r.Read())
            {
                lineId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                lineQty = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
            }
        }
        if (qty < -Eps)
            return SqlServerDatabase.SaveResult.Fail("Количество не может быть отрицательным.", rev);
        if (!outOfKp && ActQuantityPolicy.ExceedsKp(qty, qtyByKp, closedByOthers, lineQty, Eps))
            return SqlServerDatabase.SaveResult.Fail(
                $"Сверх КП: доступно {ActQuantityPolicy.Remaining(qtyByKp, closedByOthers):0.######} ({name[..Math.Min(60, name.Length)]})", rev);

        var old = lineQty;
        if (qty <= Eps)
        {
            if (lineId is not null)
            {
                Execute(conn, tx, "DELETE FROM dbo.act_lines WHERE id=@id", lineId.Value);
                if (outOfKp)
                    CleanupOrphanExtra(conn, tx, positionId);
            }
        }
        else if (lineId is null)
        {
            using var insert = new SqlCommand(
                "INSERT dbo.act_lines (act_id,position_id,qty,qty_formula,price,work_price) " +
                "VALUES (@act,@pos,@qty,N'',@price,@workPrice)", conn, tx);
            insert.Parameters.AddWithValue("@act", actId);
            insert.Parameters.AddWithValue("@pos", positionId);
            insert.Parameters.AddWithValue("@qty", (double)qty);
            insert.Parameters.AddWithValue("@price", (double)actPrice);
            insert.Parameters.AddWithValue("@workPrice", (double)actWorkPrice);
            insert.ExecuteNonQuery();
            if (outOfKp)
                using (var upd = new SqlCommand(
                    "UPDATE dbo.positions SET qty=@q WHERE id=@id", conn, tx))
                {
                    upd.Parameters.AddWithValue("@q", (double)TotalLinesFor(conn, tx, positionId));
                    upd.Parameters.AddWithValue("@id", positionId);
                    upd.ExecuteNonQuery();
                }
        }
        else
        {
            using var update = new SqlCommand(
                "UPDATE dbo.act_lines SET qty=@q WHERE id=@id", conn, tx);
            update.Parameters.AddWithValue("@q", (double)qty);
            update.Parameters.AddWithValue("@id", lineId.Value);
            update.ExecuteNonQuery();
            if (outOfKp)
                using (var upd = new SqlCommand(
                    "UPDATE dbo.positions SET qty=@q WHERE id=@id", conn, tx))
                {
                    upd.Parameters.AddWithValue("@q", (double)TotalLinesFor(conn, tx, positionId));
                    upd.Parameters.AddWithValue("@id", positionId);
                    upd.ExecuteNonQuery();
                }
        }

        if (Math.Abs(old - qty) > Eps)
        {
            var actTitle = ActTitle(conn, tx, actId);
            using (var log = new SqlCommand(
                "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details) " +
                "VALUES (@ts,@user,N'act',@id,N'line',@details)", conn, tx))
            {
                log.Parameters.AddWithValue("@ts", DateTime.Now);
                log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
                log.Parameters.AddWithValue("@id", actId);
                log.Parameters.AddWithValue("@details",
                    $"{actTitle}: {name[..Math.Min(60, name.Length)]} {old:0.######} → {qty:0.######}");
                log.ExecuteNonQuery();
            }
        }
        SqlServerDatabase.SyncActWork(conn, tx, new[] { actId });
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Количество с формулой (acts.apply_quantities): строка с формулой «@…» плюс
    /// зависимые пересчёты строк с формулами; проверка остатков по каждой
    /// изменяемой строке (allowOver — подтверждённое «сверх КП», ks_view._confirm_over);
    /// всё в одной транзакции с meta.rev.
    /// </summary>
    public static SqlServerDatabase.SaveResult SetQtyWithFormula(string connectionString,
        long actId, long positionId, decimal qty, string formula, string expectedRevision,
        bool allowOver = false)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обнови КС.", rev);

        bool signed, contractLocked;
        long contractId;
        using (var read = new SqlCommand(
            "SELECT a.signed, a.contract_id, c.locked FROM dbo.acts a " +
            "JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", actId);
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            signed = Convert.ToBoolean(r.GetValue(0), CultureInfo.InvariantCulture);
            contractId = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
            contractLocked = Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture);
        }
        if (signed || contractLocked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор изменять нельзя.", rev);

        if (!PositionBelongsToContract(conn, tx, positionId, contractId))
            return SqlServerDatabase.SaveResult.Fail("Позиция не принадлежит этому договору.", rev);
        ApplyWithFormula(conn, tx, actId, contractId, positionId, qty, formula, rev, expectedRevision, allowOver);
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Save several direct quantities in ONE transaction and bump revision only
    /// after every row and dependent formula passes validation. Failure rolls back
    /// the whole batch, unlike a series of separate SetQty calls from the UI.
    /// Outside-KP positions are never inserted by automatic FillRemaining.
    /// </summary>
    public static SqlServerDatabase.SaveResult SetQuantitiesBatch(
        string connectionString, long actId,
        IReadOnlyList<(long PositionId, decimal Qty)> changes, string expectedRevision) =>
        SaveQuantityBatchCore(connectionString, actId, changes, expectedRevision, null, null);

    /// <summary>Null SMR means calculated by positions, not a manual amount.</summary>
    public sealed record ActBatchCharges(decimal? Smr, decimal Transport, decimal Pnr);

    public static SqlServerDatabase.SaveResult SetQuantitiesAndChargesBatch(
        string connectionString, long contractId, long actId,
        IReadOnlyList<(long PositionId, decimal Qty)> changes, ActBatchCharges charges,
        string expectedRevision)
    {
        if (contractId <= 0 || actId <= 0 || charges is null ||
            new decimal?[] { charges.Smr, charges.Transport, charges.Pnr }
                .Any(v => v is decimal amount && (amount < 0m || amount > 1_000_000_000_000m)))
            return SqlServerDatabase.SaveResult.Fail("Недопустимые реквизиты или суммы КС-2.", expectedRevision);
        return SaveQuantityBatchCore(connectionString, actId, changes, expectedRevision, contractId, charges);
    }

    private static SqlServerDatabase.SaveResult SaveQuantityBatchCore(
        string connectionString, long actId,
        IReadOnlyList<(long PositionId, decimal Qty)> changes, string expectedRevision,
        long? expectedContractId, ActBatchCharges? charges)
    {
        if (changes is null || (changes.Count == 0 && charges is null))
            return SqlServerDatabase.SaveResult.Fail("Нет строк для изменения.", expectedRevision);
        if (changes.Any(c => c.PositionId <= 0 || c.Qty < 0m) ||
            changes.Select(c => c.PositionId).Distinct().Count() != changes.Count)
            return SqlServerDatabase.SaveResult.Fail("Список объёмов содержит неверные или повторные позиции.", expectedRevision);
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите КС.", rev);

        long contractId;
        string smrMode;
        using (var command = new SqlCommand(
            "SELECT a.contract_id,a.signed,c.locked,ISNULL(c.smr_mode,N'') FROM dbo.acts a " +
            "JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id", conn, tx))
        {
            command.Parameters.AddWithValue("@id", actId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            contractId = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            smrMode = reader.GetString(3);
            if (expectedContractId is long expected && contractId != expected)
                return SqlServerDatabase.SaveResult.Fail("Акт относится к другому договору.", rev);
            if (Convert.ToBoolean(reader.GetValue(1), CultureInfo.InvariantCulture) ||
                Convert.ToBoolean(reader.GetValue(2), CultureInfo.InvariantCulture))
                return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор изменять нельзя.", rev);
        }

        if (charges is not null && ((smrMode == "pos") != (charges.Smr is null)))
            return SqlServerDatabase.SaveResult.Fail("Режим расчёта СМР изменился. Обновите КС.", rev);

        if(charges is not null&&SqlSchemaCompatibility.HasPreviewColumns(conn))
        {
            using var manual=new SqlCommand("SELECT section_amounts_json FROM dbo.acts WHERE id=@id",conn,tx);
            manual.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            if(!string.IsNullOrEmpty(Convert.ToString(manual.ExecuteScalar(),CultureInfo.InvariantCulture)))
                return SqlServerDatabase.SaveResult.Fail("Сначала отредактируйте ручную разбивку СМР/транспорта по разделам; пакет не меняет её автоматически.",rev);
        }
        foreach (var (positionId, qty) in changes)
        {
            if (!PositionBelongsToContract(conn, tx, positionId, contractId))
                throw new InvalidOperationException($"Позиция {positionId} не относится к договору акта.");
            ApplyWithFormula(conn, tx, actId, contractId, positionId, qty,
                "", rev, expectedRevision);
        }

        if (charges is not null)
        {
            // Keep the header, section settings and calculated positional SMR intact.
            // A failure here rolls back quantities and their dependent formula changes.
            using var update = new SqlCommand(
                "UPDATE dbo.acts SET smr_amount=CASE WHEN @smr IS NULL THEN smr_amount ELSE @smr END," +
                "transport_amount=@tr,pnr_amount=@pnr WHERE id=@id AND contract_id=@cid AND signed=0", conn, tx);
            update.Parameters.Add("@smr", SqlDbType.Float).Value = charges.Smr is decimal smr
                ? (object)(double)smr : DBNull.Value;
            update.Parameters.Add("@tr", SqlDbType.Float).Value = (double)charges.Transport;
            update.Parameters.Add("@pnr", SqlDbType.Float).Value = (double)charges.Pnr;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            update.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                throw new DBConcurrencyException("Не удалось сохранить суммы акта.");
        }

        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (SYSDATETIME(),@user,N'act',@id,N'edit',@details,@cid)", conn, tx))
        {
            log.Parameters.Add("@user", SqlDbType.NVarChar, 255).Value = ChangeLog.CurrentUser;
            log.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            log.Parameters.Add("@cid", SqlDbType.BigInt).Value = contractId;
            log.Parameters.Add("@details", SqlDbType.NVarChar, -1).Value = charges is null
                ? $"Пакет объёмов: {changes.Count} строк"
                : $"Пакет объёмов и сумм: {changes.Count} строк; СМР={charges.Smr?.ToString("0.00", CultureInfo.InvariantCulture) ?? "по позициям"}; транспорт={charges.Transport:0.00}; ПНР={charges.Pnr:0.00}";
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Применить правку с формулой внутри открытой транзакции (acts.apply_quantities):
    /// строка редактирования + зависимые пересчёты строк с формулами (act_qty.calculate),
    /// проверка остатков по каждой строке (вне КП — свободно), запись, журнал act/line,
    /// СМР «по поз.» пересчитывается. Откат по исключению делает вызывающий.
    /// </summary>
    private static void ApplyWithFormula(SqlConnection conn, SqlTransaction tx, long actId,
        long contractId, long positionId, decimal qty, string formula, string rev,
        string expectedRevision, bool allowOver = false)
    {
        var lines = new Dictionary<long, (string QtyFormula, decimal Qty)>();
        using (var read = new SqlCommand(
            "SELECT position_id, ISNULL(qty_formula,N''), qty FROM dbo.act_lines WHERE act_id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", actId);
            using var r = read.ExecuteReader();
            while (r.Read())
                lines[Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture)] = (
                    r.GetString(1),
                    r.IsDBNull(2) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture)));
        }
        var sources = new HashSet<long>();
        using (var read = new SqlCommand(
            "SELECT p.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@cid AND p.parent_id IS NULL AND p.out_of_kp=0", conn, tx))
        {
            read.Parameters.AddWithValue("@cid", contractId);
            using var r = read.ExecuteReader();
            while (r.Read())
                sources.Add(Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture));
        }

        var edits = new Dictionary<long, (decimal Qty, string Formula)>
        {
            [positionId] = (qty, formula),
        };
        var calculated = ActQuantityFormulas.Calculate(lines, edits, sources);

        var closedByOthers = ClosedByActs(conn, contractId, actId, tx);
        var pending = new List<(long PositionId, decimal Qty, string Formula, bool FromEdit)>();
        // формула правки сохраняется всегда (в т.ч. числовая «=12*1,15»), количество —
        // пересчитанное по ссылкам, если строка в calculated (acts.apply_quantities)
        pending.Add((positionId,
            calculated.TryGetValue(positionId, out var ownQty) ? ownQty : qty,
            formula, true));
        foreach (var (pid, newQty) in calculated)
        {
            if (pid == positionId) continue;
            var oldQty = lines.TryGetValue(pid, out var line) ? line.Qty : 0m;
            if (Math.Abs(newQty - oldQty) <= 0.000000000001m) continue;
            pending.Add((pid, newQty, formulas0(lines, pid), false));
        }

        foreach (var (pid, newQty, rowFormula, fromEdit) in pending)
        {
            if (!PositionBelongsToContract(conn, tx, pid, contractId))
                throw new InvalidOperationException($"Позиция {pid} из формулы не относится к договору.");
            decimal qtyByKp = 0m;
            bool outOfKp;
            using (var read = new SqlCommand(
                "SELECT qty, out_of_kp FROM dbo.positions WHERE id=@id", conn, tx))
            {
                read.Parameters.AddWithValue("@id", pid);
                using var r = read.ExecuteReader();
                if (!r.Read()) throw new InvalidOperationException("Позиция формулы отсутствует в базе.");
                qtyByKp = r.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
                outOfKp = !r.IsDBNull(1) && Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            }
            var closedOthers = closedByOthers.GetValueOrDefault(pid);
            var oldQty = lines.TryGetValue(pid, out var line) ? line.Qty : 0m;
            if (newQty < -Eps)
                throw new InvalidOperationException("Количество не может быть отрицательным.");
            if (!allowOver && !outOfKp && ActQuantityPolicy.ExceedsKp(newQty, qtyByKp, closedOthers, oldQty, Eps))
                throw new InvalidOperationException(
                    $"Сверх КП: доступно {ActQuantityPolicy.Remaining(qtyByKp, closedOthers):0.######}");

            decimal? newPrice = null, newWorkPrice = null;
            if (fromEdit || lines.ContainsKey(pid))
            {
                using (var read = new SqlCommand(
                    "SELECT price, work_price FROM dbo.positions WHERE id=@id", conn, tx))
                {
                    read.Parameters.AddWithValue("@id", pid);
                    using var r = read.ExecuteReader();
                    if (r.Read())
                    {
                        newPrice = r.IsDBNull(0) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(0), CultureInfo.InvariantCulture));
                        newWorkPrice = r.IsDBNull(1) ? 0m : Money.FromDouble(Convert.ToDouble(r.GetValue(1), CultureInfo.InvariantCulture));
                    }
                }
            }

            var hasLine = lines.ContainsKey(pid);
            if (newQty <= Eps)
            {
                if (hasLine)
                {
                    Execute(conn, tx, "DELETE FROM dbo.act_lines WHERE position_id=@id AND act_id=" + actId, pid);
                    if (outOfKp)
                        CleanupOrphanExtra(conn, tx, pid);
                }
            }
            else if (!hasLine)
            {
                using (var insert = new SqlCommand(
                    "INSERT dbo.act_lines (act_id,position_id,qty,qty_formula,price,work_price) " +
                    "VALUES (@act,@pos,@qty,@formula,@price,@workPrice)", conn, tx))
                {
                    insert.Parameters.AddWithValue("@act", actId);
                    insert.Parameters.AddWithValue("@pos", pid);
                    insert.Parameters.AddWithValue("@qty", (double)newQty);
                    insert.Parameters.AddWithValue("@formula", rowFormula ?? "");
                    insert.Parameters.AddWithValue("@price", (double)(newPrice ?? 0m));
                    insert.Parameters.AddWithValue("@workPrice", (double)(newWorkPrice ?? 0m));
                    insert.ExecuteNonQuery();
                }
            }
            else
            {
                using (var update = new SqlCommand(
                    "UPDATE dbo.act_lines SET qty=@q, qty_formula=@formula " +
                    "WHERE act_id=@act AND position_id=@pos", conn, tx))
                {
                    update.Parameters.AddWithValue("@q", (double)newQty);
                    update.Parameters.AddWithValue("@formula", rowFormula ?? "");
                    update.Parameters.AddWithValue("@act", actId);
                    update.Parameters.AddWithValue("@pos", pid);
                    update.ExecuteNonQuery();
                }
            }
            if (outOfKp && newQty > Eps)
                using (var update = new SqlCommand(
                    "UPDATE dbo.positions SET qty=@q WHERE id=@id", conn, tx))
                {
                    update.Parameters.AddWithValue("@q", (double)TotalLinesFor(conn, tx, pid));
                    update.Parameters.AddWithValue("@id", pid);
                    update.ExecuteNonQuery();
                }
        }
        SqlServerDatabase.SyncActWork(conn, tx, new[] { actId });
    }

    private static string formulas0(Dictionary<long, (string QtyFormula, decimal Qty)> lines, long pid) =>
        lines.TryGetValue(pid, out var line) ? line.QtyFormula : "";

    private static long? lineId1(SqlConnection conn, SqlTransaction tx, long actId, long positionId)
    {
        using var cmd = new SqlCommand(
            "SELECT TOP(1) id FROM dbo.act_lines WHERE act_id=@act AND position_id=@pos", conn, tx);
        cmd.Parameters.AddWithValue("@act", actId);
        cmd.Parameters.AddWithValue("@pos", positionId);
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Prevent cross-contract act lines when callers supply an arbitrary position ID.</summary>
    private static bool PositionBelongsToContract(SqlConnection conn, SqlTransaction tx,
        long positionId, long contractId)
    {
        using var command = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "WHERE p.id=@pid AND s.contract_id=@cid AND p.parent_id IS NULL", conn, tx);
        command.Parameters.AddWithValue("@pid", positionId);
        command.Parameters.AddWithValue("@cid", contractId);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1L;
    }

    public sealed record Ks3Report(decimal Period, decimal Year, decimal Start, decimal Vat,
        decimal Total, IReadOnlyList<(string Title, decimal Net)> Previous, bool GrossCumulative = false);

    /// <summary>
    /// Цифры КС-3 (acts.ks3_figures): без НДС — за период, за год и с начала работ
    /// (по актам документа до текущего включительно), НДС и сумма периода.
    /// Ввозимый остаток из Excel (ks3_opening marker) не перенесён — появится
    /// вместе с Excel-импортом.
    /// </summary>
    public static Ks3Report Ks3Figures(string connectionString, long actId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = SqlServerDatabase.Revision(conn);
            long contractId;
            using (var command = new SqlCommand(
                "SELECT contract_id FROM dbo.acts WHERE id=@id", conn))
            {
                command.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
                var result = command.ExecuteScalar();
                if (result is null || result is DBNull)
                    throw new InvalidOperationException("Акт не найден.");
                contractId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
            }
            var scope = SqlServerKsContractScope.Read(conn, contractId);
            var report = KsGroupReport.CalculateSingle(scope, actId);
            if (!string.IsNullOrWhiteSpace(before) && before == SqlServerDatabase.Revision(conn))
                return new Ks3Report(report.PeriodNet, report.YearNet, report.SinceStartNet,
                    report.PeriodVat, report.PeriodGross,
                    report.Previous.Select(x => ($"КС-2 № {x.Key} от {x.Date:dd.MM.yyyy}", x.Net)).ToList(),
                    report.GrossCumulative);
        }
        throw new InvalidOperationException("КС изменились во время расчёта КС-3. Повторите обновление.");
    }

    /// <summary>Use the same full gross totals as the contract KS list.</summary>
    public static Ks3Report CalculateKs3ReportFromActs(
        IReadOnlyList<ActInfo> source, long actId)
    {
        var acts = source.OrderBy(a => a.Date).ThenBy(a => a.Id).ToList();
        var index = acts.FindIndex(a => a.Id == actId);
        if (index < 0) throw new InvalidOperationException("Акт не найден.");
        var now = acts[index];
        var previous = acts.Take(index).ToList();
        return new Ks3Report(
            now.Ks3Net,
            now.Ks3Net + previous.Where(a => a.Date.Year == now.Date.Year).Sum(a => a.Ks3Net),
            now.Ks3Net + previous.Sum(a => a.Ks3Net),
            now.Vat, now.Total,
            previous.Select(a => ($"КС-2 № {a.Number} от {a.Date:dd.MM.yyyy}", a.Ks3Net))
                .ToList());
    }

    /// <summary>
    /// Testable pure KS-3 figures: selected period, year-to-date, and inception
    /// totals with the VAT amount extracted from gross values.
    /// </summary>
    public static Ks3Report CalculateKs3Report(
        IReadOnlyList<(long Id, string Number, DateTime Date, decimal Total)> acts,
        long actId, decimal vatRate)
    {
        if (vatRate < 0m)
            throw new ArgumentOutOfRangeException(nameof(vatRate));
        var index = -1;
        for (var i = 0; i < acts.Count; i++)
            if (acts[i].Id == actId) { index = i; break; }
        if (index < 0) throw new InvalidOperationException("Акт не найден.");

        var current = acts[index];
        var upto = acts.Take(index + 1).ToList();
        var inYear = upto.Where(a => a.Date.Year == current.Date.Year).ToList();
        decimal Net(decimal amount) =>
            decimal.Round(amount - amount * vatRate / (100m + vatRate), 2,
                MidpointRounding.ToEven);
        var previous = upto.Take(index)
            .Select(a => (Title: $"КС-2 № {a.Number} от {a.Date:dd.MM.yyyy}",
                Net: Net(a.Total)))
            .ToList();

        return new Ks3Report(
            Net(current.Total),
            Net(inYear.Sum(a => a.Total)),
            Net(upto.Sum(a => a.Total)),
            decimal.Round(current.Total * vatRate / (100m + vatRate), 2,
                MidpointRounding.ToEven),
            current.Total, previous);
    }

    /// <summary>Подпись акта (ks_view): подписанный акт блокируется от правок.</summary>
    public static SqlServerDatabase.SaveResult ToggleSigned(string connectionString, long actId,
        bool signed, string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        return ToggleSignedCore(conn,actId,signed,expectedRevision,SqlSchemaCompatibility.HasPreviewColumns(conn));
    }

    public static SqlServerDatabase.SaveResult ToggleSignedSchema31Test(string connectionString, long actId,
        bool signed, string expectedRevision)
    {
        using var conn = OpenSchema31WriteTest(connectionString);
        return ToggleSignedCore(conn,actId,signed,expectedRevision,true);
    }

    private static SqlServerDatabase.SaveResult ToggleSignedCore(SqlConnection conn,long actId,
        bool signed,string expectedRevision,bool schema31)
    {
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.", expectedRevision);
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите КС.", rev);

        long contractId;
        bool oldSigned, locked;
        string number;
        using (var read = new SqlCommand(
            "SELECT a.contract_id,a.signed,c.locked,ISNULL(a.number,N'') " +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id", conn, tx))
        {
            read.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            using var r = read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.", rev);
            contractId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            oldSigned = Convert.ToBoolean(r.GetValue(1), CultureInfo.InvariantCulture);
            locked = Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture);
            number = r.GetString(3);
        }
        if (oldSigned == signed)
            return new SqlServerDatabase.SaveResult(true, null, rev); // idempotent no-op, no revision bump
        if (locked)
            return SqlServerDatabase.SaveResult.Fail("Договор заблокирован; изменять подпись КС нельзя.", rev);

        using (var update = new SqlCommand(
            "UPDATE dbo.acts SET signed=@signed" +
            (schema31 && !signed
                ? ",discount_percent=CASE WHEN discount_override=0 AND manual_total_cents<0 " +
                  "THEN (SELECT discount_percent FROM dbo.contracts WHERE id=@contract) ELSE discount_percent END"
                : "") +
            " WHERE id=@id AND contract_id=@contract AND signed=@wasSigned", conn, tx))
        {
            update.Parameters.Add("@signed", SqlDbType.Bit).Value = signed;
            update.Parameters.Add("@wasSigned", SqlDbType.Bit).Value = oldSigned;
            update.Parameters.Add("@id", SqlDbType.BigInt).Value = actId;
            update.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
            if (update.ExecuteNonQuery() != 1)
                return SqlServerDatabase.SaveResult.Fail("Акт изменился; перечитайте данные.", rev);
        }
        using (var log = new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'act',@id,@action,@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", actId);
            log.Parameters.AddWithValue("@action", signed ? "sign" : "unsign");
            log.Parameters.AddWithValue("@details",
                $"КС-2 № {number}: " + (signed ? "Отмечен как подписанный" : "Снята отметка «Подписан»"));
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        var updated = SqlServerDatabase.BumpPositionRevision(conn, tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true, null, updated);
    }

    /// <summary>
    /// Строка «вне сметы» (acts.add_extra_line): позиция вне КП + строка акта одним
    /// пакетом. Раздел должен принадлежать договору акта; имя/единица обязательны,
    /// количество &gt; 0, цена ≥ 0. Журнал position/create и act/line; СМР пересчитывается.
    /// Возвращает id новой позиции.
    /// </summary>
    public static long AddExtraLine(string connectionString, long actId, long systemId,
        string name, string unit, decimal qty, decimal price, string expectedRevision)
    {
        name = (name ?? "").Trim();
        unit = (unit ?? "").Trim();
        if (name.Length == 0) throw new InvalidOperationException("Впишите наименование позиции");
        if (unit.Length == 0) throw new InvalidOperationException("Впишите единицу измерения");
        if (qty <= Eps) throw new InvalidOperationException("Количество должно быть больше нуля");
        if (price < 0) throw new InvalidOperationException("Цена не может быть отрицательной");

        RequirePilotWrite(connectionString);
        using var conn = SqlServerDatabase.Open(connectionString);
        using var tx = conn.BeginTransaction(IsolationLevel.Serializable);
        var rev = SqlServerDatabase.LockRevision(conn, tx);
        if (rev is null) throw new InvalidOperationException("Отсутствует meta.rev.");
        if (!string.Equals(rev, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Данные изменены другим сотрудником. Обнови КС.");

        long contractId;
        string actNumber = "", objectName = "";
        bool signed, contractLocked;
        using (var read = new SqlCommand(
            "SELECT a.contract_id, a.number, a.signed, o.name, c.locked " +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id " +
            "JOIN dbo.objects o ON o.id=c.object_id WHERE a.id=@id", conn, tx))
        {
            read.Parameters.AddWithValue("@id", actId);
            using var r = read.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Акт не найден.");
            contractId = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
            actNumber = r.GetString(1);
            signed = Convert.ToBoolean(r.GetValue(2), CultureInfo.InvariantCulture);
            objectName = r.IsDBNull(3) ? "" : r.GetString(3);
            contractLocked = Convert.ToBoolean(r.GetValue(4),CultureInfo.InvariantCulture);
        }
        if (signed || contractLocked)
            throw new InvalidOperationException("Подписанный акт или заблокированный договор изменять нельзя.");
        using (var check = new SqlCommand(
            "SELECT COUNT_BIG(*) FROM dbo.systems WHERE id=@sid AND contract_id=@cid", conn, tx))
        {
            check.Parameters.AddWithValue("@sid", systemId);
            check.Parameters.AddWithValue("@cid", contractId);
            if (Convert.ToInt64(check.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("Выберите систему этого договора");
        }

        long positionId;
        using (var insert = new SqlCommand(
            "INSERT dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
            "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
            "work_price,cost_note,cost_color,work_sell,work_cost) OUTPUT INSERTED.id " +
            "VALUES (@sys,NULL,NULL,N'',N'',@name,@unit,@qty,@price,NULL,N'',N'',NULL," +
            "(SELECT ISNULL(MAX([order]),-1)+1 FROM dbo.positions WHERE system_id=@sys)," +
            "1,N'',NULL,NULL,NULL,N'',N'',NULL,NULL)", conn, tx))
        {
            insert.Parameters.AddWithValue("@sys", systemId);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@unit", unit);
            insert.Parameters.AddWithValue("@qty", (double)qty);
            insert.Parameters.AddWithValue("@price", (double)price);
            positionId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        using (var line = new SqlCommand(
            "INSERT dbo.act_lines (act_id,position_id,qty,qty_formula,price,work_price) " +
            "VALUES (@act,@pos,@qty,N'',@price,0)", conn, tx))
        {
            line.Parameters.AddWithValue("@act", actId);
            line.Parameters.AddWithValue("@pos", positionId);
            line.Parameters.AddWithValue("@qty", (double)qty);
            line.Parameters.AddWithValue("@price", (double)price);
            line.ExecuteNonQuery();
        }
        SqlServerDatabase.SyncActWork(conn, tx, new[] { actId });

        var actTitle = $"КС-2 № {actNumber}";
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'position',@id,N'create',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", positionId);
            log.Parameters.AddWithValue("@details", $"Вне сметы: {name[..Math.Min(80, name.Length)]}");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        using (var log = new SqlCommand(
            "INSERT INTO dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'act',@id,N'line',@details,@contract)", conn, tx))
        {
            log.Parameters.AddWithValue("@ts", DateTime.Now);
            log.Parameters.AddWithValue("@user", ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id", actId);
            log.Parameters.AddWithValue("@details",
                $"{actTitle}: вне сметы {name[..Math.Min(60, name.Length)]} {qty:0.######} × {price:0.######}");
            log.Parameters.AddWithValue("@contract", contractId);
            log.ExecuteNonQuery();
        }
        SqlServerWorkspace.BumpRevision(conn, tx);
        tx.Commit();
        return positionId;
    }

    // ---------------------------------------------------------------- внутреннее

    private static decimal TotalLinesFor(SqlConnection conn, SqlTransaction tx, long positionId)
    {
        using var cmd = new SqlCommand(
            "SELECT COALESCE(SUM(qty),0) FROM dbo.act_lines WHERE position_id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", positionId);
        return Money.FromDouble(Convert.ToDouble(cmd.ExecuteScalar() ?? 0d, CultureInfo.InvariantCulture));
    }

    private static string ActTitle(SqlConnection conn, SqlTransaction tx, long actId)
    {
        using var cmd = new SqlCommand("SELECT number FROM dbo.acts WHERE id=@id", conn, tx);
        cmd.Parameters.AddWithValue("@id", actId);
        var number = Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
        return $"КС-2 № {number}";
    }

    private static void Execute(SqlConnection conn, SqlTransaction tx, string sql, long id)
    {
        using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    private static void RequirePilotWrite(string connectionString)
    {
        SqlServerWorkspace.RequirePilotDatabase(connectionString);
    }
}
