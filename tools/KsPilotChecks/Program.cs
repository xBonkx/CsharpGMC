using System.Data;
using System.Globalization;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class Program
{
    private static int Main()
    {
        try
        {
            var cs=Environment.GetEnvironmentVariable("GMC_KS_PILOT_CONNECTION");
            if(string.IsNullOrWhiteSpace(cs))
                throw new InvalidOperationException("NOT RUN: GMC_KS_PILOT_CONNECTION is required.");
            var builder=new SqlConnectionStringBuilder(cs);
            if(!builder.InitialCatalog.StartsWith("GmcSmetaPilot_",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("REFUSED: only GmcSmetaPilot_* test databases are writable.");
            if(!long.TryParse(Environment.GetEnvironmentVariable("GMC_KS_PILOT_CONTRACT_ID"),out var contractId) ||
               contractId<=0)
                throw new InvalidOperationException("NOT RUN: GMC_KS_PILOT_CONTRACT_ID is required.");

            using var conn=SqlServerDatabase.Open(cs);
            var realDatabase=conn.Database;
            if(!realDatabase.Equals(builder.InitialCatalog,StringComparison.OrdinalIgnoreCase) ||
               !realDatabase.StartsWith("GmcSmetaPilot_",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("REFUSED: actual SQL Server database does not match pilot.");
            Console.WriteLine("Pilot server="+conn.DataSource+" database="+conn.Database);

            var acts=SqlServerActs.ActsOf(conn,contractId);
            if(acts.Count==0)
                throw new InvalidOperationException("BLOCKED: seed at least one KS-2 before running.");
            var act=acts[^1];
            var revision=SqlServerDatabase.Revision(conn);
            var rowCount=ScalarLong(conn,
                "SELECT COUNT_BIG(*) FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
                "WHERE sy.contract_id=@id AND p.parent_id IS NULL",contractId);
            Console.WriteLine("Estimate rows="+rowCount+"; acts="+acts.Count+"; revision="+revision);
            if(rowCount<2500)
                Console.WriteLine("LIMITATION: 2500/10000-row SQL load acceptance NOT covered by this fixture.");

            // Synthetic invalid revision is guaranteed to fail BEFORE an update is attempted.
            var conflict=SqlServerActs.ToggleSigned(cs,act.Id,!act.Signed,
                "intentionally-stale-ks-pilot-revision");
            Check(!conflict.Saved,"Stale act write was incorrectly accepted.");
            Check(SqlServerDatabase.Revision(conn)==revision,"Stale write changed the revision.");
            Console.WriteLine("PASS revision conflict rejects mutation");

            // A signed KS-2 must not permit quantity/formula updates.
            var signedAct=acts.FirstOrDefault(a=>a.Signed);
            if(signedAct is not null)
            {
                var scopeRows=SqlServerActs.ActRows(conn,signedAct.Id,contractId);
                var line=scopeRows.Lines.FirstOrDefault()??scopeRows.Available.FirstOrDefault();
                if(line is not null)
                {
                    var result=SqlServerActs.SetQty(cs,signedAct.Id,line.PositionId,line.Qty,revision);
                    Check(!result.Saved,"Signed act quantity was editable.");
                    Check(SqlServerDatabase.Revision(conn)==revision,"Signed test bumped revision.");
                    Console.WriteLine("PASS signed act write protection");
                }
                else Console.WriteLine("BLOCKED signed KS line test: no material positions");
            }
            else Console.WriteLine("BLOCKED signed KS mutation test: seed a signed act");

            // Snapshot financial verification uses identical SQL act rows.
            var moneyRows=new List<(decimal Quantity,decimal Price)>();
            using(var cmd=new SqlCommand(
                "SELECT al.qty,al.price FROM dbo.act_lines al WHERE al.act_id=@act",conn))
            {
                cmd.Parameters.Add("@act",SqlDbType.BigInt).Value=act.Id;
                using var reader=cmd.ExecuteReader();
                while(reader.Read())
                    moneyRows.Add((D(reader,0),D(reader,1)));
            }
            var mat=KsFinancialPolicy.Materials(moneyRows,act.RowsExact,act.RoundRows);
            var calculated=KsFinancialPolicy.Calculate(mat,act.Smr,act.Transport,act.Pnr,
                act.VatRate,act.VatOnTop,act.GrossAdjustmentCents);
            Check(calculated.Gross==act.Total,"Act gross differs from SQL snapshot.");
            Check(calculated.Ks3Net==act.Ks3Net,"Act net differs from SQL snapshot.");
            Console.WriteLine("PASS actual SQL KS finances: net="+act.Ks3Net.ToString("0.00",
                CultureInfo.InvariantCulture)+" gross="+act.Total.ToString("0.00",CultureInfo.InvariantCulture));

            using var readCancellation = new CancellationTokenSource();
            var scope=SqlServerKsContractScope.Read(cs,contractId,readCancellation.Token);
            var baseline = scope.Documents.SelectMany(doc => SqlServerActs.ActsOf(conn, doc.Id)
                .Select(a => a with { SourceName = doc.Label })).OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
            Check(SqlServerDatabase.Revision(conn)==revision,"Concurrent edit during KS batch parity; retry the isolated check.");
            Check(scope.Acts.SequenceEqual(baseline),"Batched legal scope changed act amounts, IDs, or ordering.");
            var (totals, rules) = Totals.ReadOneWithRules(conn, checked((int)contractId), readCancellation.Token);
            var standalone = SqlServerActs.ActSheet(conn, act.Id, contractId, readCancellation.Token);
            var shared = SqlServerActs.ActSheet(conn, act.Id, contractId, readCancellation.Token, rules, totals?.Form);
            Check(standalone.Positions.SequenceEqual(shared.Positions) &&
                standalone.Systems.SequenceEqual(shared.Systems) && standalone.Form == shared.Form &&
                standalone.NumManual == shared.NumManual, "Shared rule snapshot changed act sheet.");
            Check(SqlServerDatabase.Revision(conn)==revision,"Concurrent edit during KS shared rule check.");
            Console.WriteLine("PASS bounded KS scope and shared rules parity");
            var key=KsWorkspaceProjection.GroupKey(act);
            var group=KsGroupReport.Calculate(scope,key);
            Check(group.PeriodGross==group.Acts.Sum(a=>a.Total),
                "Grouped KS-3 period gross is inconsistent.");
            var snapshot=SqlServerKsPrintData.Read(cs,contractId,key,readCancellation.Token);
            Check(snapshot.Report.PeriodGross==group.PeriodGross,
                "SQL document snapshot differs from KS-3.");
            Console.WriteLine("PASS SQL KS-3 and print snapshot: "+snapshot.Lines.Count+" rows");

            // Exercise the same revision row under SERIALIZABLE then force rollback.
            // The update never commits and may not be run on any production database.
            var previous=revision;
            using(var transaction=conn.BeginTransaction(IsolationLevel.Serializable))
            {
                using var read=new SqlCommand(
                    "SELECT [value] FROM dbo.meta WITH (UPDLOCK,HOLDLOCK) WHERE [key]=N'rev'",
                    conn,transaction);
                var value=Convert.ToString(read.ExecuteScalar(),CultureInfo.InvariantCulture);
                Check(value==previous,"Revision changed between pilot assertions.");
                using var update=new SqlCommand(
                    "UPDATE dbo.meta SET [value]=@probe WHERE [key]=N'rev'",conn,transaction);
                update.Parameters.AddWithValue("@probe","ks_rollback_probe");
                Check(update.ExecuteNonQuery()==1,"Rollback probe found no meta.rev.");
                transaction.Rollback();
            }
            Check(SqlServerDatabase.Revision(conn)==previous,"Rollback did not restore SQL rev.");
            Console.WriteLine("PASS SQL SERIALIZABLE transaction rollback");

            // Two physically different clients reading the same legal agreement.
            var t1=Task.Run(()=>SqlServerKsContractScope.Read(cs,contractId));
            var t2=Task.Run(()=>SqlServerKsContractScope.Read(cs,contractId));
            Task.WaitAll(t1,t2);
            Check(t1.Result.AgreementNumber==t2.Result.AgreementNumber,
                "Two SQL clients resolved different legal agreements.");
            Check(t1.Result.Acts.Select(a=>a.Id).SequenceEqual(t2.Result.Acts.Select(a=>a.Id)),
                "Two SQL clients saw different acts.");
            Console.WriteLine("PASS dual-client SQL snapshot consistency");

            // Explicit opt-in only: create and clean disposable KS drafts on a
            // dedicated pilot contract. Never run on production or by default CI.
            if(Environment.GetEnvironmentVariable("GMC_KS_PILOT_MUTATION_SUITE")=="YES")
                RunDisposableExtrasSuite(cs,conn,contractId);
            else
                Console.WriteLine("NOT RUN: extra-line write suite requires GMC_KS_PILOT_MUTATION_SUITE=YES");

            // Unknown-COMMIT network fault injection still requires a controlled
            // proxy/chaos environment; this runner does not disconnect a live server.
            return 0;
        }
        catch(Exception error)
        {
            Console.Error.WriteLine("SQL PILOT NOT ACCEPTED: "+error);
            return 1;
        }
    }

    /// <summary>
    /// Committing checks for an isolated dedicated pilot contract: creates TWO
    /// temporary KS-2, shares an extra, signs one, copy-on-write edits another.
    /// Uses only the application's SQL APIs and deletes everything on success.
    /// If a network failure occurs during COMMIT, stops instead of replaying.
    /// </summary>
    private static void RunDisposableExtrasSuite(string cs,SqlConnection conn,long contractId)
    {
        long actA=0,actB=0,oldPosition=0,clonedPosition=0;
        string Rev()=>SqlServerDatabase.Revision(conn);
        var runId="KS-PILOT-"+Guid.NewGuid().ToString("N")[..10];
        using var section=new SqlCommand(
            "SELECT TOP(1) id FROM dbo.systems WHERE contract_id=@cid AND parent_id IS NULL " +
            "ORDER BY [order],id",conn);
        section.Parameters.Add("@cid",SqlDbType.BigInt).Value=contractId;
        var value=section.ExecuteScalar();
        if(value is null)
            throw new InvalidOperationException("BLOCKED: no root system for disposable KS act test.");
        var systemId=Convert.ToInt64(value,CultureInfo.InvariantCulture);
        try
        {
            actA=SqlServerActs.Create(cs,contractId,Rev());
            actB=SqlServerActs.Create(cs,contractId,Rev());
            oldPosition=SqlServerActs.AddExtraLine(cs,actA,systemId,
                runId,"шт",2m,3m,Rev());
            var share=SqlServerActs.SetQty(cs,actB,oldPosition,1m,Rev());
            Check(share.Saved,"Cannot attach disposable shared extra to second KS act.");
            var sign=SqlServerActs.ToggleSigned(cs,actB,true,Rev());
            Check(sign.Saved,"Cannot sign disposable second KS act.");
            var signedRows=SqlServerActs.ActRows(conn,actB,contractId);
            var signedLine=signedRows.Lines.Single(x=>x.PositionId==oldPosition);
            Check(signedLine.Price==3m,"Signed act did not freeze its original extra price.");

            // Editing only price must NOT change shared position nor signed act.
            var sameId=SqlServerActs.UpdateExtraLine(cs,actA,oldPosition,
                runId,"шт",4m,Rev());
            Check(sameId==oldPosition,"Price-only change should not clone a shared position.");
            Check(SqlServerActs.ActRows(conn,actB,contractId).Lines
                .Single(x=>x.PositionId==oldPosition).Price==3m,
                "Signed act price was overwritten by other act's edit.");

            // Name change requires copy-on-write.
            clonedPosition=SqlServerActs.UpdateExtraLine(cs,actA,oldPosition,
                runId+"-new","шт",5m,Rev());
            Check(clonedPosition!=oldPosition,"Shared extra name edit failed to clone position.");
            var aRow=SqlServerActs.ActRows(conn,actA,contractId).Lines
                .Single(x=>x.PositionId==clonedPosition);
            var bRow=SqlServerActs.ActRows(conn,actB,contractId).Lines
                .Single(x=>x.PositionId==oldPosition);
            Check(aRow.Name==runId+"-new" && aRow.Price==5m,
                "Edited draft row is wrong after copy-on-write.");
            Check(bRow.Name==runId && bRow.Price==3m,
                "Signed row changed after other act's copy-on-write.");

            var cannotDelete=SqlServerActs.Delete(cs,actB,Rev());
            Check(!cannotDelete.Saved,"Signed KS deletion was erroneously allowed.");
            var cannotEdit=SqlServerActs.SetQty(cs,actB,oldPosition,3m,Rev());
            Check(!cannotEdit.Saved,"Signed KS quantity change was erroneously allowed.");

            var dropA=SqlServerActs.DeleteExtraLine(cs,actA,clonedPosition,Rev());
            Check(dropA.Saved,"Unable to delete draft's independent extra position.");
            Check(ScalarLong(conn,
                "SELECT COUNT_BIG(*) FROM dbo.positions WHERE id=@id",clonedPosition)==0,
                "Orphan cloned position remains after deletion.");

            var unsign=SqlServerActs.ToggleSigned(cs,actB,false,Rev());
            Check(unsign.Saved,"Cannot unsign disposable act.");
            var dropB=SqlServerActs.DeleteExtraLine(cs,actB,oldPosition,Rev());
            Check(dropB.Saved,"Unable to clean shared extra after last act.");
            Check(ScalarLong(conn,
                "SELECT COUNT_BIG(*) FROM dbo.positions WHERE id=@id",oldPosition)==0,
                "Original orphan extra remains after last act.");

            Check(SqlServerActs.Delete(cs,actA,Rev()).Saved,"Cannot clean act A.");
            actA=0;
            Check(SqlServerActs.Delete(cs,actB,Rev()).Saved,"Cannot clean act B.");
            actB=0;
            Console.WriteLine("PASS disposable SQL extra-line copy-on-write, signed and orphan cleanup");
        }
        catch
        {
            Console.Error.WriteLine("PILOT TEST INCOMPLETE. Inspect disposable act IDs "+
                actA+" / "+actB+" and position IDs "+oldPosition+" / "+clonedPosition+
                ". Unknown commits must be reconciled before manual cleanup.");
            throw;
        }
    }

    private static long ScalarLong(SqlConnection connection,string sql,long contract)
    {
        using var cmd=new SqlCommand(sql,connection);
        cmd.Parameters.Add("@id",SqlDbType.BigInt).Value=contract;
        return Convert.ToInt64(cmd.ExecuteScalar(),CultureInfo.InvariantCulture);
    }

    private static decimal D(IDataRecord reader,int ordinal) => reader.IsDBNull(ordinal) ? 0m :
        Money.FromDouble(Convert.ToDouble(reader.GetValue(ordinal),CultureInfo.InvariantCulture));

    private static void Check(bool condition,string message)
    {
        if(!condition) throw new InvalidDataException(message);
    }
}
