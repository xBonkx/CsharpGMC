using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// KS-specific draft metadata/charges. Separate from estimate, stage and cost editors.
/// Every write holds the SQL Server revision lock, validates the legal document
/// and appends an audit entry before committing one atomic transaction.
/// </summary>
public static partial class SqlServerActs
{
    public sealed record ActDetails(
        long Id, long ContractId, string Number, DateTime Date,
        DateTime PeriodFrom, DateTime PeriodTo, bool Signed, bool ContractLocked,
        decimal Smr, decimal Transport, decimal Pnr, bool SmrBySystem, string SmrMode,
        string DsNo = "", string DsDate = "", string DsExtra = "", string SectionAmountsJson = "",
        decimal DiscountPercent = 0m, bool DiscountOverride = false, long ManualTotalCents = -1,
        bool SupportsAdjustment = false);

    public sealed record ActDraftEdit(
        string Number, DateTime Date, DateTime PeriodFrom, DateTime PeriodTo,
        decimal Smr, decimal Transport, decimal Pnr, bool SmrBySystem,
        string DsNo = "", string DsDate = "", string DsExtra = "");

    public static ActDetails ReadDetails(string connectionString, long actId)
    {
        using var conn = SqlServerDatabase.Open(connectionString);
        return ReadDetails(conn, actId);
    }

    public static ActDetails ReadDetails(SqlConnection conn, long actId)
    {
        var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
        using var cmd = new SqlCommand(
            "SELECT a.id,a.contract_id,a.number,a.[date],a.period_from,a.period_to," +
            "a.signed,c.locked,ISNULL(a.smr_amount,0),ISNULL(a.transport_amount,0)," +
            "ISNULL(a.pnr_amount,0),ISNULL(a.smr_by_system,0),ISNULL(c.smr_mode,N'')," +
            "ISNULL(a.ds_no,N''),ISNULL(a.ds_date,N''),ISNULL(a.ds_extra,N'')" +
            (schema31 ? ",a.section_amounts_json,a.discount_percent,a.discount_override,a.manual_total_cents,c.form,o.name,t.rules " : " ") +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id "+
            (schema31 ? "JOIN dbo.objects o ON o.id=c.object_id LEFT JOIN dbo.tags t ON t.[key]=c.form " : "")+
            "WHERE a.id=@id",conn);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
        using var r=cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("Акт не найден.");
        decimal D(int i) => r.IsDBNull(i) ? 0m :
            Money.FromDouble(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture));
        var date = r.IsDBNull(3) ? DateTime.Today : r.GetDateTime(3);
        string S(int i)=>r.IsDBNull(i)?"":Convert.ToString(r.GetValue(i),CultureInfo.InvariantCulture)??"";
        return new ActDetails(
            Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),Convert.ToInt64(r.GetValue(1),CultureInfo.InvariantCulture),r.IsDBNull(2) ? "" : r.GetString(2),
            date,r.IsDBNull(4) ? date : r.GetDateTime(4),
            r.IsDBNull(5) ? date : r.GetDateTime(5),
            !r.IsDBNull(6) && r.GetBoolean(6),
            !r.IsDBNull(7) && r.GetBoolean(7), D(8),D(9),D(10),
            !r.IsDBNull(11) && r.GetBoolean(11),
            r.IsDBNull(12) ? "" : r.GetString(12),
            r.IsDBNull(13) ? "" : r.GetString(13),
            r.IsDBNull(14) ? "" : r.GetString(14),
            r.IsDBNull(15) ? "" : r.GetString(15),
            schema31 && !r.IsDBNull(16) ? r.GetString(16) : "",
            schema31 ? DiscountPolicy.ReadPercent(r, 17) : 0m,
            schema31 && !r.IsDBNull(18) && Convert.ToBoolean(r.GetValue(18)),
            schema31 && !r.IsDBNull(19) ? Convert.ToInt64(r.GetValue(19)) : -1L,
            schema31 && DiscountPolicy.SupportsDiscount(KsFinancialPolicy.HasRule(S(22),"vat_top"),
                KsFinancialPolicy.HasRule(S(22),"ks_dns"),S(20),S(21)));
    }

    /// <summary>Validate before opening a SQL transaction; includes Python's date normalization.</summary>
    public static ActDraftEdit NormalizeDraft(ActDraftEdit edit)
    {
        var number=(edit.Number ?? "").Trim();
        if (number.Length is 0 or > 32)
            throw new ArgumentException("Укажите номер КС-2 (не более 32 символов).");
        if (edit.Date == default)
            throw new ArgumentException("Укажите дату акта.");
        var date=edit.Date.Date;
        var to=edit.PeriodTo.Date > date ? date : edit.PeriodTo.Date;
        var from=edit.PeriodFrom.Date > to ? to : edit.PeriodFrom.Date;
        if (from == default || to == default)
            throw new ArgumentException("Укажите начало и конец периода.");
        foreach (var v in new[] { edit.Smr, edit.Transport, edit.Pnr })
            if (v < 0m || v > 1000000000000m)
                throw new ArgumentOutOfRangeException(nameof(edit),"СМР, транспорт и ПНР должны быть неотрицательными.");
        return edit with { Number=number, Date=date, PeriodFrom=from, PeriodTo=to };
    }

    /// <summary>
    /// Save draft headers and charges in one revision-checked unit.
    /// Signed acts and signed/locked contracts are never modified.
    /// In 'pos' mode SMR derives from row work prices, and manual overrides are rejected.
    /// </summary>
    public static SqlServerDatabase.SaveResult SaveDraft(string connectionString,
        long actId, ActDraftEdit edit, string expectedRevision)
    {
        edit=NormalizeDraft(edit);
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if (rev is null)
            return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.",expectedRevision);
        if (!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены другим сотрудником. Обновите КС.",rev);

        long contractId;
        bool signed,locked;
        string smrMode;
        decimal oldSmr,oldTransport=0m;
        bool oldBySystem=false,manualSections=false;
        var schema31=SqlSchemaCompatibility.HasPreviewColumns(conn);
        using (var read=new SqlCommand(
            "SELECT a.contract_id,a.signed,c.locked,ISNULL(c.smr_mode,N''),ISNULL(a.smr_amount,0) " +
            (schema31?",a.transport_amount,a.smr_by_system,a.section_amounts_json ":"")+
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id",conn,tx))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=read.ExecuteReader();
            if (!r.Read()) return SqlServerDatabase.SaveResult.Fail("Акт не найден.",rev);
            contractId=Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture);
            signed=r.GetBoolean(1);
            locked=r.GetBoolean(2);
            smrMode=r.IsDBNull(3) ? "" : r.GetString(3);
            oldSmr=Money.FromDouble(Convert.ToDouble(r.GetValue(4),CultureInfo.InvariantCulture));
            if(schema31)
            {
                oldTransport=Money.FromDouble(Convert.ToDouble(r.GetValue(5),CultureInfo.InvariantCulture));
                oldBySystem=Convert.ToBoolean(r.GetValue(6));
                manualSections=!r.IsDBNull(7)&&!string.IsNullOrEmpty(r.GetString(7));
            }
        }
        if (signed || locked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор изменять нельзя.",rev);
        if (smrMode=="pos" && edit.Smr!=oldSmr)
            return SqlServerDatabase.SaveResult.Fail("СМР «по позициям» вычисляются по строкам, ручной ввод запрещён.",rev);

        if(manualSections&&(edit.Smr!=oldSmr||edit.Transport!=oldTransport||edit.SmrBySystem!=oldBySystem))
            return SqlServerDatabase.SaveResult.Fail("Суммы с ручной разбивкой меняются через «СМР и транспорт по разделам».",rev);

        using (var update=new SqlCommand(
            "UPDATE dbo.acts SET number=@number,[date]=@date,period_from=@from,period_to=@to," +
            "smr_amount=@smr,transport_amount=@tr,pnr_amount=@pnr,smr_by_system=@bySystem," +
            "ds_no=@dsNo,ds_date=@dsDate,ds_extra=@dsExtra " +
            "WHERE id=@id AND contract_id=@cid AND signed=0",conn,tx))
        {
            update.Parameters.Add("@number",SqlDbType.NVarChar,32).Value=edit.Number;
            update.Parameters.Add("@date",SqlDbType.Date).Value=edit.Date;
            update.Parameters.Add("@from",SqlDbType.Date).Value=edit.PeriodFrom;
            update.Parameters.Add("@to",SqlDbType.Date).Value=edit.PeriodTo;
            update.Parameters.Add("@smr",SqlDbType.Float).Value=(double)edit.Smr;
            update.Parameters.Add("@tr",SqlDbType.Float).Value=(double)edit.Transport;
            update.Parameters.Add("@pnr",SqlDbType.Float).Value=(double)edit.Pnr;
            update.Parameters.Add("@bySystem",SqlDbType.Bit).Value=edit.SmrBySystem;
            update.Parameters.Add("@dsNo",SqlDbType.NVarChar,32).Value=edit.DsNo ?? "";
            update.Parameters.Add("@dsDate",SqlDbType.NVarChar,16).Value=edit.DsDate ?? "";
            update.Parameters.Add("@dsExtra",SqlDbType.NVarChar,-1).Value=edit.DsExtra ?? "";
            update.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            update.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
            if (update.ExecuteNonQuery()!=1)
                return SqlServerDatabase.SaveResult.Fail("Акт изменён или удалён другим сотрудником.",rev);
        }
        using (var log=new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@ts,@user,N'act',@id,N'edit',@details,@contract)",conn,tx))
        {
            log.Parameters.AddWithValue("@ts",DateTime.Now);
            log.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
            log.Parameters.AddWithValue("@id",actId);
            log.Parameters.AddWithValue("@details",
                $"КС-2 № {edit.Number}: дата/период, СМР={edit.Smr:0.00}, транспорт={edit.Transport:0.00}, ПНР={edit.Pnr:0.00}");
            log.Parameters.AddWithValue("@contract",contractId);
            log.ExecuteNonQuery();
        }
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        // A failed/unknown COMMIT is deliberately propagated; the caller must reload, not retry blindly.
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }

    /// <summary>Allocate KS-2 SMR to systems as acts.smr_by_system() in Python.</summary>
    public static IReadOnlyDictionary<long,decimal> SmrBySystem(string connectionString,long actId)
    {
        using var conn=SqlServerDatabase.Open(connectionString);
        decimal smr;
        long contractId;
        string mode, form, sectionJson;
        var schema31 = SqlSchemaCompatibility.HasPreviewColumns(conn);
        using (var read=new SqlCommand(
            "SELECT ISNULL(a.smr_amount,0),ISNULL(c.smr_mode,N''),ISNULL(c.form,N''),a.contract_id" +
            (schema31 ? ",a.section_amounts_json " : " ") +
            "FROM dbo.acts a JOIN dbo.contracts c ON c.id=a.contract_id WHERE a.id=@id",conn))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=read.ExecuteReader();
            if (!r.Read()) throw new InvalidOperationException("Акт не найден.");
            smr=Money.FromDouble(Convert.ToDouble(r.GetValue(0),CultureInfo.InvariantCulture));
            mode=r.IsDBNull(1) ? "" : r.GetString(1);
            form=r.IsDBNull(2) ? "" : r.GetString(2);
            contractId=Convert.ToInt64(r.GetValue(3),CultureInfo.InvariantCulture);
            sectionJson=schema31 && !r.IsDBNull(4) ? r.GetString(4) : "";
        }
        if (mode != "pos" && KsActAllocation.ReadManual(sectionJson) is { } saved)
        {
            var valid = new HashSet<long>();
            using var systems = new SqlCommand("SELECT id FROM dbo.systems WHERE contract_id=@id", conn);
            systems.Parameters.Add("@id",SqlDbType.BigInt).Value=contractId;
            using var rows = systems.ExecuteReader();
            while (rows.Read()) valid.Add(Convert.ToInt64(rows.GetValue(0),CultureInfo.InvariantCulture));
            return saved.Smr.Where(x => x.Key != 0 && valid.Contains(x.Key))
                .ToDictionary(x=>x.Key,x=>x.Value);
        }
        string? rules=null;
        using (var tag = new SqlCommand("SELECT rules FROM dbo.tags WHERE [key]=@key", conn))
        {
            tag.Parameters.Add("@key", SqlDbType.NVarChar, 255).Value=form;
            rules=Convert.ToString(tag.ExecuteScalar(),CultureInfo.InvariantCulture);
        }
        var lines=new List<KsActAllocation.Line>();
        using (var read=new SqlCommand(
            "SELECT sy.id,al.qty,al.price,al.work_price FROM dbo.act_lines al " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id AND sy.contract_id=a.contract_id " +
            "WHERE al.act_id=@id ORDER BY sy.[order],sy.id,p.[order],p.id",conn))
        {
            read.Parameters.Add("@id",SqlDbType.BigInt).Value=actId;
            using var r=read.ExecuteReader();
            while (r.Read())
                lines.Add(new KsActAllocation.Line(
                    Convert.ToInt64(r.GetValue(0),CultureInfo.InvariantCulture),
                    Money.FromDouble(Convert.ToDouble(r.GetValue(1),CultureInfo.InvariantCulture)),
                    Money.FromDouble(Convert.ToDouble(r.GetValue(2),CultureInfo.InvariantCulture)),
                    Money.FromDouble(Convert.ToDouble(r.GetValue(3),CultureInfo.InvariantCulture))));
        }
        return KsActAllocation.Split(lines,smr,mode=="pos",
            KsFinancialPolicy.HasRule(rules,"round_rows"));
    }
}
