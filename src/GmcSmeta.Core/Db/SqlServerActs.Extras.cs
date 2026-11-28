using System.Data;
using System.Globalization;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Draft-only extra KS-2 lines: copy-on-write when other (possibly signed)
/// acts share a position, safe orphan cleanup and act-specific row ordering.
/// </summary>
public static partial class SqlServerActs
{
    private sealed record ExtraContext(long LineId,long ContractId,long SystemId,
        int Order,string Name,string Unit,decimal Quantity,decimal Price,
        bool Signed,bool Locked);

    private static ExtraContext? LoadExtra(SqlConnection conn,SqlTransaction tx,
        long actId,long positionId)
    {
        using var cmd=new SqlCommand(
            "SELECT al.id,a.contract_id,p.system_id,ISNULL(p.[order],0),p.name,p.unit," +
            "al.qty,al.price,a.signed,c.locked " +
            "FROM dbo.act_lines al WITH (UPDLOCK,HOLDLOCK) " +
            "JOIN dbo.acts a ON a.id=al.act_id " +
            "JOIN dbo.contracts c WITH (HOLDLOCK) ON c.id=a.contract_id " +
            "JOIN dbo.objects o WITH (HOLDLOCK) ON o.id=c.object_id " +
            "JOIN dbo.positions p ON p.id=al.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id AND sy.contract_id=a.contract_id " +
            "WHERE al.act_id=@act AND al.position_id=@position " +
            "AND p.parent_id IS NULL AND p.out_of_kp=1 " +
            "AND ISNULL(o.archived,0)=0 AND ISNULL(c.stage_rest,0)=0",conn,tx);
        cmd.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
        cmd.Parameters.Add("@position",SqlDbType.BigInt).Value=positionId;
        using var r=cmd.ExecuteReader();
        if (!r.Read()) return null;
        decimal D(int i) => r.IsDBNull(i) ? 0m :
            Money.FromDouble(Convert.ToDouble(r.GetValue(i),CultureInfo.InvariantCulture));
        return new ExtraContext(
            SqlInteger.ReadInt64(r, 0),
            SqlInteger.ReadInt64(r, 1),
            SqlInteger.ReadInt64(r, 2),
            SqlInteger.ReadInt32(r, 3),
            r.IsDBNull(4) ? "" : r.GetString(4),r.IsDBNull(5) ? "" : r.GetString(5),
            D(6),D(7),SqlFlag.Read(r, 8),
            SqlFlag.Read(r, 9));
    }

    private static bool SharedExtra(SqlConnection conn,SqlTransaction tx,long actId,long positionId)
    {
        using var cmd=new SqlCommand(
            "SELECT TOP(1) al.id FROM dbo.act_lines al WITH (UPDLOCK,HOLDLOCK) " +
            "WHERE al.position_id=@position AND al.act_id<>@act",conn,tx);
        cmd.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
        cmd.Parameters.Add("@position",SqlDbType.BigInt).Value=positionId;
        return cmd.ExecuteScalar() is not null;
    }

    private static void LogExtra(SqlConnection conn,SqlTransaction tx,
        long actId,long contractId,string action,string details)
    {
        using var cmd=new SqlCommand(
            "INSERT dbo.change_log (ts,[user],entity,entity_id,action,details,contract_id) " +
            "VALUES (@time,@user,N'act',@act,@action,@details,@contract)",conn,tx);
        cmd.Parameters.AddWithValue("@time",DateTime.Now);
        cmd.Parameters.AddWithValue("@user",ChangeLog.CurrentUser);
        cmd.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
        cmd.Parameters.AddWithValue("@action",action);
        cmd.Parameters.AddWithValue("@details",details);
        cmd.Parameters.Add("@contract",SqlDbType.BigInt).Value=contractId;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Delete an unused out-of-KP position ONLY when it has no KS, procurement,
    /// movements, shipment or child-position references. Never cascade user data.
    /// Shared extras keep a quantity equal to remaining act-line quantities.
    /// </summary>
    private static bool CleanupOrphanExtra(SqlConnection conn,SqlTransaction tx,long positionId)
    {
        using var check=new SqlCommand(
            "SELECT CASE WHEN EXISTS(SELECT 1 FROM dbo.act_lines WHERE position_id=@id) " +
            "OR EXISTS(SELECT 1 FROM dbo.procurement WHERE position_id=@id) " +
            "OR EXISTS(SELECT 1 FROM dbo.movements WHERE position_id=@id) " +
            "OR EXISTS(SELECT 1 FROM dbo.shipment_lines WHERE position_id=@id) " +
            "OR EXISTS(SELECT 1 FROM dbo.positions WHERE parent_id=@id) " +
            "THEN 1 ELSE 0 END",conn,tx);
        check.Parameters.Add("@id",SqlDbType.BigInt).Value=positionId;
        var referenced=Convert.ToInt32(check.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;
        if (!referenced)
        {
            using var delete=new SqlCommand(
                "DELETE FROM dbo.positions WHERE id=@id AND out_of_kp=1",conn,tx);
            delete.Parameters.Add("@id",SqlDbType.BigInt).Value=positionId;
            return delete.ExecuteNonQuery()==1;
        }
        using var update=new SqlCommand(
            "UPDATE dbo.positions SET qty=ISNULL((" +
            "SELECT ROUND(SUM(al.qty),6) FROM dbo.act_lines al WHERE al.position_id=@id),0) " +
            "WHERE id=@id AND out_of_kp=1",conn,tx);
        update.Parameters.Add("@id",SqlDbType.BigInt).Value=positionId;
        update.ExecuteNonQuery();
        return false;
    }

    /// <summary>
    /// Equivalent to Python update_extra_line: edit the current act only.
    /// A changed shared name/unit clones the position and rebinds this act's
    /// line, protecting every other act (including signed acts).
    /// Returns the current position ID, which may change after clone.
    /// </summary>
    public static long UpdateExtraLine(string connectionString,long actId,long positionId,
        string name,string unit,decimal price,string expectedRevision)
    {
        name=(name??"").Trim();
        unit=(unit??"").Trim();
        if (name.Length==0) throw new ArgumentException("Наименование не может быть пустым.");
        if (unit.Length==0 || unit.Length>32) throw new ArgumentException("Некорректная единица измерения.");
        if (price<0m || price>1000000000000m)
            throw new ArgumentOutOfRangeException(nameof(price));
        if (string.IsNullOrWhiteSpace(expectedRevision))
            throw new DBConcurrencyException("Нет подтверждённой ревизии КС.");
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if (!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            throw new DBConcurrencyException("Ревизия документа изменена; обновите КС.");
        var row=LoadExtra(conn,tx,actId,positionId)
            ?? throw new InvalidOperationException("Дополнительная строка не принадлежит этому акту.");
        if (row.Signed || row.Locked)
            throw new InvalidOperationException("Подписанный акт или заблокированный договор изменять нельзя.");
        var shared=SharedExtra(conn,tx,actId,positionId);
        var labelsChanged=!string.Equals(name,row.Name,StringComparison.Ordinal) ||
            !string.Equals(unit,row.Unit,StringComparison.Ordinal);
        var targetId=positionId;
        if (shared && labelsChanged)
        {
            // Preserve every other field of this position (notes, pricing and
            // report metadata), but assign this KS-2 its own new top-level ID.
            using (var shift=new SqlCommand(
                "UPDATE dbo.positions SET [order]=[order]+1 " +
                "WHERE system_id=@system AND [order]>@order",conn,tx))
            {
                shift.Parameters.Add("@system",SqlDbType.BigInt).Value=row.SystemId;
                shift.Parameters.Add("@order",SqlDbType.Int).Value=row.Order;
                shift.ExecuteNonQuery();
            }
            using (var clone=new SqlCommand(
                "INSERT dbo.positions (system_id,parent_id,catalog_item_id,num,basis,name,unit,qty,price," +
                "purchase_price,note,extra_json,source_row,[order],out_of_kp,formulas,stage_src,area_manual," +
                "work_price,cost_note,cost_color,work_sell,work_cost) OUTPUT INSERTED.id " +
                "SELECT system_id,NULL,catalog_item_id,N'',basis,@name,@unit,@qty,@price," +
                "purchase_price,note,extra_json,source_row,@order,1,formulas,stage_src,area_manual," +
                "work_price,cost_note,cost_color,work_sell,work_cost FROM dbo.positions " +
                "WHERE id=@old AND out_of_kp=1",conn,tx))
            {
                clone.Parameters.Add("@old",SqlDbType.BigInt).Value=positionId;
                clone.Parameters.Add("@name",SqlDbType.NVarChar,-1).Value=name;
                clone.Parameters.Add("@unit",SqlDbType.NVarChar,32).Value=unit;
                clone.Parameters.Add("@qty",SqlDbType.Float).Value=(double)row.Quantity;
                clone.Parameters.Add("@price",SqlDbType.Float).Value=(double)price;
                clone.Parameters.Add("@order",SqlDbType.Int).Value=row.Order+1;
                targetId=SqlInteger.RequireInt64(clone.ExecuteScalar());
            }
            using (var relink=new SqlCommand(
                "UPDATE dbo.act_lines SET position_id=@new,price=@price " +
                "WHERE id=@line AND act_id=@act AND position_id=@old",conn,tx))
            {
                relink.Parameters.Add("@new",SqlDbType.BigInt).Value=targetId;
                relink.Parameters.Add("@old",SqlDbType.BigInt).Value=positionId;
                relink.Parameters.Add("@line",SqlDbType.BigInt).Value=row.LineId;
                relink.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
                relink.Parameters.Add("@price",SqlDbType.Float).Value=(double)price;
                if(relink.ExecuteNonQuery()!=1)
                    throw new DBConcurrencyException("Позиция акта изменилась.");
            }
            CleanupOrphanExtra(conn,tx,positionId);
        }
        else
        {
            if (!shared)
                using (var position=new SqlCommand(
                    "UPDATE dbo.positions SET name=@name,unit=@unit,price=@price " +
                    "WHERE id=@id AND out_of_kp=1",conn,tx))
                {
                    position.Parameters.Add("@name",SqlDbType.NVarChar,-1).Value=name;
                    position.Parameters.Add("@unit",SqlDbType.NVarChar,32).Value=unit;
                    position.Parameters.Add("@price",SqlDbType.Float).Value=(double)price;
                    position.Parameters.Add("@id",SqlDbType.BigInt).Value=positionId;
                    if(position.ExecuteNonQuery()!=1) throw new DBConcurrencyException("Позиция исчезла.");
                }
            // For a shared line price is ALWAYS local to ActLine.
            using var line=new SqlCommand(
                "UPDATE dbo.act_lines SET price=@price WHERE id=@id AND act_id=@act",conn,tx);
            line.Parameters.Add("@price",SqlDbType.Float).Value=(double)price;
            line.Parameters.Add("@id",SqlDbType.BigInt).Value=row.LineId;
            line.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
            if(line.ExecuteNonQuery()!=1) throw new DBConcurrencyException("Строка акта исчезла.");
        }
        LogExtra(conn,tx,actId,row.ContractId,"line",
            $"Дополнительная строка КС-2: {name[..Math.Min(name.Length,80)]}, {unit}, {price:0.00}");
        SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit();
        return targetId;
    }

    public static SqlServerDatabase.SaveResult MoveExtraLine(string connectionString,
        long actId,long positionId,int direction,string expectedRevision,
        long? anchorId=null,bool before=false)
    {
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if(rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.",expectedRevision);
        if(!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите КС.",rev);
        var row=LoadExtra(conn,tx,actId,positionId);
        if(row is null) return SqlServerDatabase.SaveResult.Fail("Позиция не найдена в акте.",rev);
        if(row.Signed || row.Locked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт изменять нельзя.",rev);
        if(SharedExtra(conn,tx,actId,positionId))
            return SqlServerDatabase.SaveResult.Fail("Позиция используется в других актах; порядок менять нельзя.",rev);
        var roots=new List<KsExtraOrdering.Row>();
        using (var read=new SqlCommand(
            "SELECT p.id,p.out_of_kp,CASE WHEN al.id IS NULL THEN 0 ELSE 1 END " +
            "FROM dbo.positions p LEFT JOIN dbo.act_lines al ON al.position_id=p.id AND al.act_id=@act " +
            "WHERE p.system_id=@system AND p.parent_id IS NULL ORDER BY p.[order],p.id",conn,tx))
        {
            read.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
            read.Parameters.Add("@system",SqlDbType.BigInt).Value=row.SystemId;
            using var r=read.ExecuteReader();
            while(r.Read()) roots.Add(new KsExtraOrdering.Row(
                SqlInteger.ReadInt64(r, 0),
                SqlFlag.Read(r, 1),
                SqlInteger.ReadInt32(r, 2)==1));
        }
        IReadOnlyList<long> ordered;
        try { ordered=KsExtraOrdering.Plan(roots,positionId,direction,anchorId,before); }
        catch(ArgumentException ex) { return SqlServerDatabase.SaveResult.Fail(ex.Message,rev); }
        catch(InvalidOperationException ex) { return SqlServerDatabase.SaveResult.Fail(ex.Message,rev); }
        if(roots.Select(r=>r.Id).SequenceEqual(ordered))
            return new SqlServerDatabase.SaveResult(true,null,rev);
        for(int i=0;i<ordered.Count;i++)
        {
            using var update=new SqlCommand(
                "UPDATE dbo.positions SET [order]=@n WHERE id=@id AND system_id=@sys",conn,tx);
            update.Parameters.Add("@n",SqlDbType.Int).Value=i;
            update.Parameters.Add("@id",SqlDbType.BigInt).Value=ordered[i];
            update.Parameters.Add("@sys",SqlDbType.BigInt).Value=row.SystemId;
            if(update.ExecuteNonQuery()!=1)
                throw new DBConcurrencyException("При перемещении пропала позиция.");
        }
        LogExtra(conn,tx,actId,row.ContractId,"line",$"Позиция {positionId}: изменение порядка в КС-2");
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }

    public static SqlServerDatabase.SaveResult DeleteExtraLine(string connectionString,
        long actId,long positionId,string expectedRevision)
    {
        RequirePilotWrite(connectionString);
        using var conn=SqlServerDatabase.Open(connectionString);
        using var tx=conn.BeginTransaction(IsolationLevel.Serializable);
        var rev=SqlServerDatabase.LockRevision(conn,tx);
        if(rev is null) return SqlServerDatabase.SaveResult.Fail("Отсутствует meta.rev.",expectedRevision);
        if(!string.Equals(rev,expectedRevision,StringComparison.Ordinal))
            return SqlServerDatabase.SaveResult.Fail("Данные изменены. Обновите КС.",rev);
        var row=LoadExtra(conn,tx,actId,positionId);
        if(row is null) return SqlServerDatabase.SaveResult.Fail("Дополнительная строка отсутствует.",rev);
        if(row.Signed || row.Locked)
            return SqlServerDatabase.SaveResult.Fail("Подписанный акт или заблокированный договор изменять нельзя.",rev);
        using(var delete=new SqlCommand(
            "DELETE FROM dbo.act_lines WHERE id=@id AND act_id=@act AND position_id=@pid",conn,tx))
        {
            delete.Parameters.Add("@id",SqlDbType.BigInt).Value=row.LineId;
            delete.Parameters.Add("@act",SqlDbType.BigInt).Value=actId;
            delete.Parameters.Add("@pid",SqlDbType.BigInt).Value=positionId;
            if(delete.ExecuteNonQuery()!=1)throw new DBConcurrencyException("Строка акта была изменена.");
        }
        CleanupOrphanExtra(conn,tx,positionId);
        SqlServerDatabase.SyncActWork(conn,tx,new[]{actId});
        LogExtra(conn,tx,actId,row.ContractId,"line",
            $"КС-2: удалена дополнительная строка «{row.Name[..Math.Min(row.Name.Length,60)]}»");
        var updated=SqlServerDatabase.BumpPositionRevision(conn,tx);
        tx.Commit();
        return new SqlServerDatabase.SaveResult(true,null,updated);
    }
}
