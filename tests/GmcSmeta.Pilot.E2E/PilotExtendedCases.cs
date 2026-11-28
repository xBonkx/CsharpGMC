using System.Data;
using System.Diagnostics;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

// Additional SQL integration checks against public Core services. All direct SQL below
// is restricted to synthetic fixture preparation and independent assertions.
internal static class PilotExtendedCases
{
    public static void SchemaInventory(PilotRunner p)
    {
        using var conn = p.Conn();
        PilotRunner.Check(PilotRunner.Text(conn,
            "SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'") == "28",
            "The SQL Server fixture is not labelled v28");
        var mandatory = new[] { "meta", "objects", "contracts", "systems", "positions",
            "catalog_items", "acts", "act_lines", "change_log", "tags", "suppliers",
            "procurement", "movements", "shipments", "shipment_lines", "smr_costs", "__pilot_provenance" };
        foreach (var name in mandatory)
            PilotRunner.Check(PilotRunner.Long(conn,
                "SELECT COUNT_BIG(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id " +
                "WHERE s.name=N'dbo' AND t.name=@name", ("@name", name)) == 1,
                "Missing synthetic SQL table " + name);
        // The full reference has 26 domain tables. This smoke check deliberately
        // does NOT claim that a subset with a v28 marker is the full migrated schema.
    }

    public static void ZeroNegativeAndStaleWrites(PilotRunner p)
    {
        var ids = FirstIds(p, p.LargeSystem, 4);
        using var conn = p.Conn();
        // Do not mutate the first two rows used by the procurement scenario.
        var edited = ids[2];
        var untouched = ids[3];
        var rev = p.Revision();
        var old = PilotRunner.Double(conn,
            "SELECT qty FROM dbo.positions WHERE id=@id", ("@id", edited));
        var invalid = SqlServerDatabase.SaveCell(p.PilotConnection, edited, "qty", "-1", rev);
        PilotRunner.Check(!invalid.Saved && p.Revision() == rev, "Negative quantity changed meta.rev");
        PilotRunner.Check(PilotRunner.Double(conn,
            "SELECT qty FROM dbo.positions WHERE id=@id", ("@id", edited)) == old,
            "Negative quantity changed the stored row");
        var zero = SqlServerDatabase.SaveCell(p.PilotConnection, edited, "qty", "0", rev);
        PilotRunner.Check(zero.Saved, "Zero quantity should be a supported value");
        var changedRev = p.Revision();
        PilotRunner.Check(changedRev != rev, "Successful zero quantity did not bump meta.rev");
        var stale = SqlServerDatabase.SaveCell(p.PilotConnection, untouched, "price", "888", rev);
        PilotRunner.Check(!stale.Saved && p.Revision() == changedRev,
            "Stale client wrote after a revision change");
        PilotRunner.Check(PilotRunner.Double(conn,
            "SELECT price FROM dbo.positions WHERE id=@id", ("@id", untouched)) == 100d,
            "Stale client overwrote manually seeded selling price");
    }

    public static void StageRestAndComponents(PilotRunner p)
    {
        // Keep this scenario small: a separate document prevents 10k-row rest
        // creation from hiding the correctness checks and isolates signed acts.
        var contract = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-STAGE", "", "QA component stages",
                null, null), p.Revision());
        var section = SqlServerDatabase.AddSection(p.PilotConnection, contract,
            "QA-stage-system", false, null, p.Revision());
        PilotRunner.Check(section.Saved && section.SystemId.HasValue,
            "Cannot prepare independent stage fixture");
        var pos = SqlServerDatabase.AddPosition(p.PilotConnection, contract,
            section.SystemId.Value, null, null, p.Revision());
        PilotRunner.Check(pos.Saved && pos.PositionId.HasValue, "Cannot prepare stage row");
        var src = pos.PositionId.Value;
        PilotRunner.Check(SqlServerDatabase.SaveCell(p.PilotConnection,
            src, "qty", "2", p.Revision()).Saved, "Cannot set full quantity");
        PilotRunner.Check(SqlServerDatabase.SaveCell(p.PilotConnection,
            src, "price", "105.25", p.Revision()).Saved, "Cannot set manual price");

        long component;
        using (var conn = p.Conn())
            component = PilotRunner.Long(conn,
                "INSERT dbo.positions(system_id,parent_id,name,unit,qty,price,purchase_price,work_price,[order]) " +
                "OUTPUT INSERTED.id VALUES(@system,@parent,N'QA synthetic component',N'шт',4,12.5,8,1,1)",
                ("@system", section.SystemId.Value), ("@parent", src));

        var stage = SqlServerStages.CreateStage(p.PilotConnection, contract, p.Revision());
        var rest = SqlServerStages.CreateRest(p.PilotConnection, contract, p.Revision());
        var saved = SqlServerStages.SetStageQty(p.PilotConnection, stage, src, 1m, p.Revision());
        PilotRunner.Check(saved.Saved, "Public stage allocation failed");
        using var verify = p.Conn();
        var table = SqlServerStages.StageTable(verify, contract);
        var row = table.Rows.Single(x => x.SrcId == src);
        PilotRunner.Check(row.Qty == 2m && row.PerStage.Length == 1 &&
            row.PerStage[0] == 1m && row.Rest == 1m,
            "Stage/rest does not conserve the source quantity");
        var stageChild = PilotRunner.Double(verify,
            "SELECT TOP(1) p.qty FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "WHERE s.contract_id=@stage AND p.stage_src=@src AND p.parent_id IS NOT NULL",
            ("@stage", stage), ("@src", component));
        PilotRunner.Check(stageChild == 2d, "Component quantity was not scaled 4*(1/2)");
        var restQty = PilotRunner.Double(verify,
            "SELECT TOP(1) p.qty FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "WHERE s.contract_id=@rest AND p.stage_src=@src AND p.parent_id IS NULL",
            ("@rest", rest), ("@src", src));
        PilotRunner.Check(restQty == 1d, "Rest document not synchronized");
        var before = p.Revision();
        var stale = SqlServerStages.SetStageQty(p.PilotConnection, stage, src, 3m, before,
            expectedStageQty: 1m, expectedRest: 2m);
        PilotRunner.Check(!stale.Saved && p.Revision() == before,
            "A stale allocation snapshot was accepted");
        var negative = SqlServerStages.SetStageQty(p.PilotConnection, stage, src, -1m, before);
        PilotRunner.Check(!negative.Saved && p.Revision() == before,
            "Negative stage quantity changed the database");
        // WPF confirms overflow before passing its current row snapshot to the writer.
        var over = SqlServerStages.SetStageQty(p.PilotConnection, stage, src, 3m, before,
            expectedStageQty: 1m, expectedRest: 1m);
        PilotRunner.Check(over.Saved && p.Revision() != before &&
            SqlServerStages.StageTable(verify, contract).Rows.Single(x => x.SrcId == src).Rest == -1m,
            "Confirmed overflow must remain visible in the allocation table");
        PilotRunner.Check(PilotRunner.Long(verify,
            "SELECT COUNT_BIG(*) FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id " +
            "WHERE s.contract_id=@rest AND p.stage_src=@src",
            ("@rest", rest), ("@src", src)) == 0,
            "Rest document contains a negative quantity after confirmed overflow");
    }

    public static void ProcurementAndIssuedList(PilotRunner p)
    {
        var cs = p.PilotConnection;
        var contract = checked((int)SqlServerWorkspace.CreateEmptyContract(cs, p.ObjectId,
            new SqlServerWorkspace.NewContractData("main", "QA-ISSUE", "", "QA issue contract",
                null, null), p.Revision()));
        var section = SqlServerDatabase.AddSection(cs, contract, "QA issue system", false, null, p.Revision());
        PilotRunner.Check(section.Saved && section.SystemId.HasValue, "Cannot prepare issue system");
        var ids = new List<long>();
        for (var i = 0; i < 2; i++)
        {
            var position = SqlServerDatabase.AddPosition(cs, contract, section.SystemId.Value, null, null, p.Revision());
            PilotRunner.Check(position.Saved && position.PositionId.HasValue, "Cannot prepare issue position");
            ids.Add(position.PositionId.Value);
        }
        PilotRunner.Check(SqlServerDatabase.SaveCell(cs, ids[0], "purchase_price", "25", p.Revision()).Saved,
            "Cannot prepare manual purchase price");
        using var verify = p.Conn();
        var beforeManualPrice = PilotRunner.Double(verify,
            "SELECT purchase_price FROM dbo.positions WHERE id=@id", ("@id", ids[0]));
        var saved = SqlServerProcurement.SetProc(p.PilotConnection, contract, ids,
            "QA SYNTHETIC SUPPLIER", "ordered", "QA ORDER", null, 41.25m, null, null,
            "Only synthetic data", false, p.Revision());
        PilotRunner.Check(saved.Saved, "SetProc rejected valid two-row batch");
        var revisionBeforeReceipts = p.Revision();
        var movements = new[] {
            new SqlServerProcurement.MovementInput(ids[0], 1.25m),
            new SqlServerProcurement.MovementInput(ids[1], 0.75m)
        };
        var receipt = SqlServerProcurement.AddMovements(p.PilotConnection, contract,
            movements, "in", DateTime.Today, "QA-RECEIPT", "", revisionBeforeReceipts);
        PilotRunner.Check(receipt.Saved, "Public two-row receipt did not commit");
        var stale = SqlServerProcurement.AddMovements(p.PilotConnection, contract,
            movements, "in", DateTime.Today, "QA-DOUBLECLICK", "", revisionBeforeReceipts);
        PilotRunner.Check(!stale.Saved, "Same-revision double click duplicated the receipt");
        var issued = new[] {
            new SqlServerProcurement.MovementInput(ids[0], 0.5m),
            new SqlServerProcurement.MovementInput(ids[1], 0.25m)
        };
        var beforeIssue = p.Revision();
        var issue = SqlServerProcurement.IssueViaListBatch(p.PilotConnection, contract,
            issued, DateTime.Today, "QA RECIPIENT", false, beforeIssue);
        PilotRunner.Check(issue.ShipmentId > 0, "Public issue-list API did not create a sheet");
        var rejected = false;
        try
        {
            _ = SqlServerProcurement.IssueViaListBatch(p.PilotConnection, contract,
                issued, DateTime.Today, "QA RECIPIENT", false, beforeIssue);
        }
        catch (DBConcurrencyException) { rejected = true; }
        PilotRunner.Check(rejected, "Stale repeated issue-list submission was not rejected");
        PilotRunner.Check(PilotRunner.Long(verify,
            "SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id=@id",
            ("@id", issue.ShipmentId)) == 2, "Issue list does not contain two FK-backed lines");
        PilotRunner.Check(PilotRunner.Long(verify,
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE shipment_id=@id AND kind=N'out'",
            ("@id", issue.ShipmentId)) == 2,
            "Issue movements were not persisted atomically");
        var projected = SqlServerProcurement.BuyRows(p.PilotConnection, contract);
        var emptySection = SqlServerDatabase.AddSection(cs, contract, "QA empty procurement section",
            false, null, p.Revision());
        PilotRunner.Check(emptySection.Saved && emptySection.SystemId.HasValue,
            "Cannot prepare empty system for snapshot check");
        var snapshot = SqlServerProcurement.ReadBuySnapshot(cs, contract);
        var separateSystems = SqlServerProcurement.SystemChoices(cs, contract);
        PilotRunner.Check(SqlServerProcurement.ReadBuySnapshotIfChanged(cs, contract,
            snapshot.Revision, snapshot.Day) is null,
            "Unchanged same-day procurement snapshot must skip full read");
        var previousDay = SqlServerProcurement.ReadBuySnapshotIfChanged(cs, contract,
            snapshot.Revision, snapshot.Day.AddDays(-1));
        PilotRunner.Check(previousDay is not null && previousDay.Rows.Count == snapshot.Rows.Count,
            "Day rollover must reload delivery warnings");
        var staleRevision = SqlServerProcurement.ReadBuySnapshotIfChanged(cs, contract,
            revisionBeforeReceipts, snapshot.Day);
        PilotRunner.Check(staleRevision is not null && staleRevision.Revision == snapshot.Revision,
            "A stale revision must read current procurement data");
        PilotRunner.Check(snapshot.Revision == p.Revision() &&
            snapshot.Systems.SequenceEqual(separateSystems) &&
            snapshot.Systems.Any(x => x.Id == emptySection.SystemId.Value) &&
            snapshot.Rows.All(x => x.SystemId != emptySection.SystemId.Value),
            "Combined snapshot must preserve the revision and empty systems");
        PilotRunner.Check(snapshot.Rows.Count == projected.Count &&
            snapshot.Rows.Zip(projected).All(pair =>
                pair.First.Flags.SequenceEqual(pair.Second.Flags) &&
                (pair.First with { Flags = pair.Second.Flags }) == pair.Second),
            "Combined snapshot changed procurement rows, flags or ordering");
        var first = projected.Single(x => x.PositionId == ids[0]);
        PilotRunner.Check(first.Received == 1.25m && first.Issued == 0.5m &&
            first.CostPrice == 41.25m, "Real procurement projection disagrees with its ledger");
        PilotRunner.Check(PilotRunner.Double(verify,
            "SELECT purchase_price FROM dbo.positions WHERE id=@id", ("@id", ids[0]))
            == beforeManualPrice, "Supplier edit overwrote manual purchase price");

        // Compare grouped shipment reads with the independent correlated ledger.
        var candidates = SqlServerShipments.IssuableRows(verify, p.ObjectId);
        var shipmentLines = SqlServerShipments.LinesOf(verify, issue.ShipmentId);
        decimal Net(long id, string plus, string minus) => Money.FromDouble(PilotRunner.Double(verify,
            "SELECT ISNULL((SELECT SUM(qty) FROM dbo.movements WHERE position_id=@id AND kind=@plus),0)" +
            "-ISNULL((SELECT SUM(qty) FROM dbo.movements WHERE position_id=@id AND kind=@minus),0)",
            ("@id", id), ("@plus", plus), ("@minus", minus)));
        foreach (var id in ids)
        {
            var candidate = candidates.Single(x => x.PositionId == id);
            var line = shipmentLines.Single(x => x.PositionId == id);
            PilotRunner.Check(candidate.Received == Net(id, "in", "back_supplier") &&
                candidate.Issued == Net(id, "out", "back") && line.Issued == candidate.Issued,
                "Grouped shipment read disagrees with ledger");
        }
        var delta = SqlServerShipments.ReadChangedRows(cs, p.ObjectId, issue.ShipmentId, ids, p.Revision());
        PilotRunner.Check(delta is not null &&
            delta.Candidates.SequenceEqual(candidates.Where(x => ids.Contains(x.PositionId))) &&
            delta.Lines.SequenceEqual(shipmentLines.Where(x => ids.Contains(x.PositionId))),
            "Shipment targeted read differs from full snapshot");
        PilotRunner.Check(SqlServerShipments.ReadChangedRows(cs, p.ObjectId, issue.ShipmentId, ids, beforeIssue) is null,
            "Stale shipment revision accepted");

        // A mixed-contract batch must be rejected wholly, including its valid row.
        var rev = p.Revision();
        var countBefore = PilotRunner.Long(verify,
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id=@id", ("@id", ids[0]));
        var invalid = SqlServerProcurement.AddMovements(p.PilotConnection, contract,
            new[] { new SqlServerProcurement.MovementInput(ids[0], 0.2m),
                    new SqlServerProcurement.MovementInput(p.FirstPosition, 0.2m) },
            "in", DateTime.Today, "QA-CROSS-CONTRACT", "", rev);
        PilotRunner.Check(!invalid.Saved && p.Revision() == rev, "Cross-contract batch committed");
        PilotRunner.Check(PilotRunner.Long(verify,
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id=@id", ("@id", ids[0]))
            == countBefore, "Cross-contract batch persisted a partial receipt");
        var zero = SqlServerProcurement.AddMovements(p.PilotConnection, contract,
            new[] { new SqlServerProcurement.MovementInput(ids[0], 0m) },
            "in", DateTime.Today, "", "", rev);
        var negative = SqlServerProcurement.AddMovements(p.PilotConnection, contract,
            new[] { new SqlServerProcurement.MovementInput(ids[0], -1m) },
            "in", DateTime.Today, "", "", rev);
        PilotRunner.Check(!zero.Saved && !negative.Saved && p.Revision() == rev,
            "Invalid movement quantities were not rejected fail-closed");
    }

    public static void PublicSignedActAndKs3(PilotRunner p)
    {
        var src = FirstIds(p, p.LargeSystem, 6)[5];
        long act;
        using (var fixture = p.Conn())
            act = PilotRunner.Long(fixture,
                "INSERT dbo.acts(contract_id,number,[date],period_from,period_to,signed) " +
                "OUTPUT INSERTED.id VALUES(@contract,N'QA KS-2',CAST(GETDATE() AS date)," +
                "CAST(GETDATE() AS date),CAST(GETDATE() AS date),0)",
                ("@contract", p.LargeContract));
        var quantity = SqlServerActs.SetQty(p.PilotConnection, act, src, 1m, p.Revision());
        PilotRunner.Check(quantity.Saved, "Public KS-2 SetQty failed");
        // Existing synthetic act: a failed second row must not change the first row, charges or revision.
        var atomicRev = p.Revision();
        var before = SqlServerActs.ReadDetails(p.PilotConnection, act);
        var charges = new SqlServerActs.ActBatchCharges(before.SmrMode == "pos" ? null : 17m, 23m, 29m);
        var rejectedBatch = false;
        try
        {
            var bad = SqlServerActs.SetQuantitiesAndChargesBatch(p.PilotConnection, p.LargeContract, act,
                new[] { (PositionId: src, Qty: 0m), (PositionId: long.MaxValue, Qty: 1m) },
                charges, atomicRev);
            rejectedBatch = !bad.Saved;
        }
        catch (InvalidOperationException) { rejectedBatch = true; }
        using (var fixture = p.Conn())
        {
            PilotRunner.Check(rejectedBatch && p.Revision() == atomicRev &&
                PilotRunner.Double(fixture, "SELECT qty FROM dbo.act_lines WHERE act_id=@a AND position_id=@p",
                    ("@a", act), ("@p", src)) == 1d, "Combined batch did not roll back quantity");
            var unchanged = SqlServerActs.ReadDetails(fixture, act);
            PilotRunner.Check(unchanged.Smr == before.Smr && unchanged.Transport == before.Transport &&
                unchanged.Pnr == before.Pnr, "Combined batch changed charges on failure");
        }
        var combined = SqlServerActs.SetQuantitiesAndChargesBatch(p.PilotConnection, p.LargeContract, act,
            new[] { (PositionId: src, Qty: 1m) }, charges, atomicRev);
        PilotRunner.Check(combined.Saved && combined.Revision == p.Revision() &&
            combined.Revision != atomicRev, "Combined quantity/charge write failed");
        var committed = SqlServerActs.ReadDetails(p.PilotConnection, act);
        PilotRunner.Check(committed.Transport == 23m && committed.Pnr == 29m &&
            (charges.Smr is null || committed.Smr == charges.Smr.Value) && committed.Number == before.Number,
            "Combined batch did not preserve header and save charges");
        var scopedRev = p.Revision();
        PilotRunner.Check(!SqlServerActs.Delete(p.PilotConnection, act, scopedRev, p.SmallContract).Saved &&
            !SqlServerActs.ToggleSigned(p.PilotConnection, act, true, scopedRev, p.SmallContract).Saved &&
            p.Revision() == scopedRev, "Wrong-contract act operation was accepted or changed revision");
        var signed = SqlServerActs.ToggleSigned(p.PilotConnection, act, true, p.Revision(), p.LargeContract);
        PilotRunner.Check(signed.Saved, "Public KS-2 signing failed");
        var rev = p.Revision();
        var rejected = SqlServerActs.SetQty(p.PilotConnection, act, src, 1.5m, rev);
        PilotRunner.Check(!rejected.Saved && p.Revision() == rev,
            "Signed KS-2 accepted quantity edit");
        var del = SqlServerActs.Delete(p.PilotConnection, act, rev);
        PilotRunner.Check(!del.Saved && p.Revision() == rev, "Signed KS-2 was deleted");
        using var verify = p.Conn();
        PilotRunner.Check(PilotRunner.Double(verify,
            "SELECT qty FROM dbo.act_lines WHERE act_id=@act AND position_id=@position",
            ("@act", act), ("@position", src)) == 1d,
            "Signed KS-2 line quantity was modified");
        var acts = SqlServerActs.ActsOf(verify, p.LargeContract);
        var current = acts.Single(x => x.Id == act);
        PilotRunner.Check(current.Signed && current.Lines == 1,
            "Real act service did not observe signed stored line");
        var ks3 = SqlServerActs.CalculateKs3ReportFromActs(acts, act);
        PilotRunner.Check(ks3.Total > 0m && ks3.Start >= ks3.Period && ks3.Vat >= 0m,
            "Core KS-3 calculation returned invalid totals");
    }

    public static void LegalDsAndKs3Opening(PilotRunner p)
    {
        // The main agreement and DS are created through public workspace APIs.
        // Acts and the importer opening marker are SYNTHETIC prerequisites,
        // after which SetQty, signing and cross-document KS-3 reads use Core APIs.
        var main = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("main", "QA-LEGAL-101", "",
                "QA synthetic legal agreement", null, null), p.Revision());
        var ds = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("dop", "QA-DS-1", "",
                "QA synthetic supplement", null, main), p.Revision());
        long Position(long contract, string sectionName)
        {
            var section = SqlServerDatabase.AddSection(p.PilotConnection, contract,
                sectionName, false, null, p.Revision());
            PilotRunner.Check(section.Saved && section.SystemId.HasValue,
                "Unable to prepare legal/DS section");
            var add = SqlServerDatabase.AddPosition(p.PilotConnection, contract,
                section.SystemId.Value, null, null, p.Revision());
            PilotRunner.Check(add.Saved && add.PositionId.HasValue,
                "Unable to prepare legal/DS position");
            PilotRunner.Check(SqlServerDatabase.SaveCell(p.PilotConnection, add.PositionId.Value,
                "price", "150", p.Revision()).Saved, "Unable to price legal/DS position");
            return add.PositionId.Value;
        }

        var mainPos = Position(main, "QA main legal rows");
        var dsPos = Position(ds, "QA additional agreement rows");
        long mainAct, dsAct;
        using (var fixture = p.Conn())
        {
            mainAct = PilotRunner.Long(fixture,
                "INSERT dbo.acts(contract_id,number,[date],period_from,period_to,signed) " +
                "OUTPUT INSERTED.id VALUES(@id,N'1',CAST(GETDATE() AS date)," +
                "CAST(GETDATE() AS date),CAST(GETDATE() AS date),0)",
                ("@id", main));
            dsAct = PilotRunner.Long(fixture,
                "INSERT dbo.acts(contract_id,number,[date],period_from,period_to,signed) " +
                "OUTPUT INSERTED.id VALUES(@id,N'2',CAST(GETDATE() AS date)," +
                "CAST(GETDATE() AS date),CAST(GETDATE() AS date),0)",
                ("@id", ds));
        }
        using (var supplementFixture = p.Conn())
            PilotRunner.Execute(supplementFixture,
                "UPDATE dbo.acts SET ds_extra=N'№2 от 10.10.2026' WHERE id=@id", ("@id", dsAct));
        using (var supplementRead = p.Conn())
            PilotRunner.Check(SqlServerActs.ActsOf(supplementRead, ds).Single(a => a.Id == dsAct).DsExtra == "№2 от 10.10.2026",
                "Public act reader lost additional supplement requisites");
        PilotRunner.Check(SqlServerActs.SetQty(p.PilotConnection, mainAct,
            mainPos, 1m, p.Revision()).Saved, "Main KS-2 quantity failed");
        PilotRunner.Check(SqlServerActs.SetQty(p.PilotConnection, dsAct,
            dsPos, 1m, p.Revision()).Saved, "Supplement KS-2 quantity failed");
        PilotRunner.Check(SqlServerActs.ToggleSigned(p.PilotConnection, mainAct,
            true, p.Revision()).Saved, "Signing main agreement act failed");
        PilotRunner.Check(SqlServerActs.ToggleSigned(p.PilotConnection, dsAct,
            true, p.Revision()).Saved, "Signing DS act failed");
        using (var fixture = p.Conn())
            PilotRunner.Execute(fixture,
                "INSERT dbo.meta([key],[value]) VALUES(@key,@value)",
                ("@key", "ks3_opening:" + p.ObjectId + ":QA-LEGAL-101"),
                ("@value", "{\"through\":\"2026-01-01\",\"gross_cents\":230000}"));

        var scope = SqlServerKsContractScope.Read(p.PilotConnection, ds);
        PilotRunner.Check(scope.AgreementNumber == "QA-LEGAL-101" &&
            scope.Documents.Count == 2 && scope.Acts.Count == 2 &&
            scope.Opening?.Gross == 2300m,
            "DS did not inherit its base legal agreement and opening marker");
        var report = SqlServerActs.Ks3Figures(p.PilotConnection, dsAct);
        PilotRunner.Check(report.Start >= 2300m && report.Year >= report.Total &&
            report.Total > 0m && report.GrossCumulative,
            "Real KS-3 gross opening balance was not included");
        using var verify = p.Conn();
        PilotRunner.Execute(verify, "UPDATE dbo.contracts SET locked=1 WHERE id=@id",
            ("@id", ds));
        var rev = p.Revision();
        var rejected = SqlServerActs.SetQty(p.PilotConnection, dsAct,
            dsPos, 0.5m, rev);
        PilotRunner.Check(!rejected.Saved && p.Revision() == rev,
            "Signed/locked DS accepted a KS quantity change");
        PilotRunner.Check(PilotRunner.Double(verify,
            "SELECT qty FROM dbo.act_lines WHERE act_id=@act AND position_id=@pos",
            ("@act", dsAct), ("@pos", dsPos)) == 1d,
            "Rejected signed DS edit changed its committed line");
    }

    public static void RealServicesBenchmark(PilotRunner p, long contractId, int expectedRows)
    {
        var contract = checked((int)contractId);
        void Measure(string name, Func<int> read)
        {
            PilotRunner.Check(read() == expectedRows, name + ": warm-up row-count mismatch");
            var times = new List<double>(21);
            for (var i = 0; i < 21; ++i)
            {
                var sw = Stopwatch.StartNew();
                var count = read();
                sw.Stop();
                PilotRunner.Check(count == expectedRows, name + ": actual API lost rows");
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            p.MetricSamples(name, expectedRows, times,
                "21 warm-cache end-to-end Core SQL API reads; no WPF/UI, network+SQL+mapping");
        }
        Measure("public_procurement_buy_rows", () =>
            SqlServerProcurement.BuyRows(p.PilotConnection, contract).Count);
        Measure("public_procurement_full_snapshot_previous", () =>
        {
            using var conn = p.Conn();
            var rev = SqlServerDatabase.Revision(conn);
            var rows = SqlServerProcurement.BuyRows(p.PilotConnection, contract);
            _ = SqlServerProcurement.SystemChoices(p.PilotConnection, contract);
            PilotRunner.Check(!string.IsNullOrEmpty(rev) && SqlServerDatabase.Revision(conn) == rev,
                "Previous snapshot benchmark revision changed");
            return rows.Count;
        });
        Measure("public_procurement_full_snapshot_batched", () =>
            SqlServerProcurement.ReadBuySnapshot(p.PilotConnection, contract).Rows.Count);
        Measure("public_cost_summary_batched", () =>
            SqlServerCostSummary.Summary(p.PilotConnection, contract).Rows.Count);
        Measure("public_ks_progress_for_contract", () =>
            SqlServerActs.ProgressForContract(p.PilotConnection, contractId).Count);
    }

    private static long[] FirstIds(PilotRunner p, long system, int count)
    {
        using var conn = p.Conn();
        using var cmd = new SqlCommand(
            "SELECT TOP (@n) id FROM dbo.positions WHERE system_id=@system AND parent_id IS NULL " +
            "ORDER BY id", conn);
        cmd.Parameters.Add("@n", SqlDbType.Int).Value = count;
        cmd.Parameters.Add("@system", SqlDbType.BigInt).Value = system;
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        PilotRunner.Check(ids.Count == count, "Synthetic fixture is missing required rows");
        return ids.ToArray();
    }
}
