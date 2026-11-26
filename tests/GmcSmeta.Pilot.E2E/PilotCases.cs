using System.Data;
using System.Diagnostics;
using System.Globalization;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

// Independent real-SQL API checks. Direct SQL is restricted to fixture setup,
// read-back assertions, fault injection and measurements (not business mutations).
internal static class PilotCases
{
    public static void RunAll(PilotRunner p)
    {
        p.RunCase("public-object-contract-section-position", () => CreateWorkspace(p));
        p.RunCase("2500-rows-bulk-fixture-read", () => SeedAndRead(p, p.SmallSystem, 2500));
        p.RunCase("10000-rows-bulk-fixture-read", () => SeedAndRead(p, p.LargeSystem, 10000));
        p.RunCase("two-real-sql-clients-revision-conflict", () => Concurrency(p));
        p.RunCase("signed-acts-prices-and-linked-lines", () => SignedActs(p));
        p.RunCase("2500-atomic-numeric-paste-and-rollback",
            () => PilotAtomicNumericPasteCases.Run(p));
        p.RunCase("mid-transaction-fault-rollback", () => Rollback(p));
        p.RunCase("unknown-commit-ack-reconciliation-simulation", () => UnknownCommit(p));
        p.RunCase("stage-public-api-and-reopen", () => Stage(p));
        p.RunCase("stage-linked-price-writers", () => StageLinkedPrices(p));
        p.RunCase("batch-120-work-prices-public-api", () => WorkPrices(p));
        p.RunCase("restart-as-fresh-dotnet-process", p.VerifyViaNewProcess);
        p.RunCase("20-public-save-cell-latencies", () => WriteLatencies(p));
        p.RunCase("subset-schema-inventory", () => PilotExtendedCases.SchemaInventory(p));
        p.RunCase("zero-negative-quantities-and-stale-client",
            () => PilotExtendedCases.ZeroNegativeAndStaleWrites(p));
        p.RunCase("stage-rest-component-allocation-and-overflow",
            () => PilotExtendedCases.StageRestAndComponents(p));
        p.RunCase("public-procurement-receipts-and-fixed-issue",
            () => PilotExtendedCases.ProcurementAndIssuedList(p));
        p.RunCase("public-signed-ks2-and-ks3-figures",
            () => PilotExtendedCases.PublicSignedActAndKs3(p));
        p.RunCase("public-main-ds-signed-ks3-opening",
            () => PilotExtendedCases.LegalDsAndKs3Opening(p));
        p.RunCase("2500-public-sql-services-benchmark",
            () => PilotExtendedCases.RealServicesBenchmark(p, p.SmallContract, 2500));
        p.RunCase("10000-public-sql-services-benchmark",
            () => PilotExtendedCases.RealServicesBenchmark(p, p.LargeContract, 10000));

        p.RunCase("batched-cost-summary-financial-parity", () => PilotCostSummaryCases.Run(p));
        p.RunCase("batched-workspace-navigation-parity", () => PilotWorkspaceNavigationCases.Run(p));

        p.RunCase("estimate-indexed-hierarchy-and-financial-parity",
            () => EstimateHierarchy(p));
        p.RunCase("batched-object-card-projection-parity", () => PilotObjectCardCases.Run(p));

        p.Skip("full-production-v28-schema-and-migrations",
            "Only a synthetic v28 subset is available in this repository; " +
            "schema_reference.sql is SQLite-oriented and is not runnable SQL Server DDL");
        p.Skip("ks-ds-full-signing-and-accumulated-balances",
            "Synthetic main/DS, KS-3 opening and signing included; original SQL Server full schema, " +
            "actual act creation workflow and golden document totals still unverified");
        p.Skip("procurement-full-financial-and-export-parity",
            "Real receipt, supplier, shipment and Core reader tested; monetary exports " +
            "and supplier requests are owned by other agents");
        p.Skip("public-api-cancellation-mid-flight",
            "The covered synchronous Core methods expose no cancellation token; " +
            "SQL client cancellation is not an application-level cancellation test");
        p.Skip("physical-disconnect-during-commit",
            "Lost acknowledgement is simulated AFTER real COMMIT; no TCP drop tested");
        p.Skip("actual-wpf-client-relaunch",
            "New .NET process verifies persisted state; WPF window requires Windows acceptance");
    }

    // A separate owned pilot fixture: no shared business document is modified.
    private static void EstimateHierarchy(PilotRunner p)
    {
        var obj = SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties("Estimate hierarchy QA", "", "", "", "", ""), p.Revision());
        var contract = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, obj,
            new SqlServerWorkspace.NewContractData("kp", "QA-HIERARCHY", "", "Hierarchy QA",
                null, null), p.Revision());
        using var conn = p.Conn();
        p.RequireOwned(conn);
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET smr_mode=N'systems', form=N'' WHERE id=@id",
            ("@id", contract));

        long Section(string name, int order, long? parent = null, int smr = 0) =>
            PilotRunner.Long(conn,
                "INSERT dbo.systems(contract_id,name,[order],parent_id,smr_amount,heading,source_num) " +
                "OUTPUT inserted.id VALUES(@doc,@name,@order,@parent,@smr,N'',N'')",
                ("@doc", contract), ("@name", name), ("@order", order),
                ("@parent", (object?)parent ?? DBNull.Value), ("@smr", smr));
        long Position(long system, string name, int order, int price, long? parent = null,
            bool outside = false) => PilotRunner.Long(conn,
                "INSERT dbo.positions(system_id,parent_id,name,[order],qty,price,out_of_kp) " +
                "OUTPUT inserted.id VALUES(@system,@parent,@name,@order,1,@price,@outside)",
                ("@system", system), ("@parent", (object?)parent ?? DBNull.Value),
                ("@name", name), ("@order", order), ("@price", price), ("@outside", outside));

        var root = Section("П1", 10);
        var other = Section("В1", 0);
        var sub = Section("П1.1", 5, root, 40);
        var sub2 = Section("П1.2", 5, root, 5);
        var parent = Position(root, "Установка", 2, 100);
        var component = Position(root, "Привод", -1, 999, parent);
        var extra = Position(root, "Вне КП", 0, 777, outside: true);
        var item = Position(sub, "Клапан", 0, 50);
        var item2 = Position(sub2, "Датчик", 0, 25);
        var otherItem = Position(other, "Решётка", 0, 20);

        var estimate = EstimateReader.Load(conn, checked((int)contract));
        var asyncEstimate = EstimateReader.LoadSqlAsync(conn, checked((int)contract),
            CancellationToken.None).GetAwaiter().GetResult();

        PilotRunner.Check(estimate.Rows.Select(r => r.Name).SequenceEqual(new[]
        {
            "В1", "Решётка", "П1", "Вне КП", "Установка", "Привод", "П1.1",
            "Клапан", "П1.2", "Датчик"
        }), "Hierarchy order, tied subsystem order or component placement changed");
        PilotRunner.Check(estimate.Rows.Select(r => r.Level).SequenceEqual(
            new[] { 0, 1, 0, 1, 1, 2, 1, 2, 1, 2 }), "Hierarchy depth changed");
        PilotRunner.Check(estimate.Rows.Select(r => r.Num).SequenceEqual(
            new[] { "1", "2", "3", "", "4", "", "5", "6", "7", "8" }),
            "Display numbering changed");
        PilotRunner.Check(estimate.Rows.Select(r => r.SortKey).SequenceEqual(Enumerable.Range(0, 10)) &&
            estimate.PositionsInOrder.Select(r => (long)r.Id).SequenceEqual(
                new[] { otherItem, extra, parent, component, item, item2 }),
            "Sort keys or ordered position IDs changed");
        PilotRunner.Check(estimate.Rows.Where(r => r.IsSystem).Select(r => r.Amount)
            .SequenceEqual(new[] { 20m, 175m, 50m, 25m }) &&
            estimate.Rows.Single(r => r.IsSystem && r.SystemId == root).Note ==
                $"СМР раздела {GmcSmeta.Core.Money.Format(45m)}" &&
            estimate.Totals.Materials == 195m,
            "Section amounts, SMR note or document material total changed");
        PilotRunner.Check(estimate.Rows.SequenceEqual(asyncEstimate.Rows) &&
            estimate.PositionsInOrder.SequenceEqual(asyncEstimate.PositionsInOrder) &&
            estimate.Totals == asyncEstimate.Totals,
            "Synchronous and asynchronous estimate projections diverged");
    }

    private static void CreateWorkspace(PilotRunner p)
    {
        using (var conn = p.Conn()) p.RequireOwned(conn);
        p.ObjectId = SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties("Synthetic E2E", "", "", "", "", ""), p.Revision());
        p.SmallContract = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-2500", "", "QA 2500 rows",
                null, null), p.Revision());
        p.LargeContract = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-10000", "", "QA 10000 rows",
                null, null), p.Revision());
        var small = SqlServerDatabase.AddSection(p.PilotConnection, p.SmallContract,
            "S-2500", false, null, p.Revision());
        var large = SqlServerDatabase.AddSection(p.PilotConnection, p.LargeContract,
            "S-10000", false, null, p.Revision());
        PilotRunner.Check(small.Saved && small.SystemId.HasValue && large.Saved &&
            large.SystemId.HasValue, "Public AddSection failed");
        p.SmallSystem = small.SystemId.Value;
        p.LargeSystem = large.SystemId.Value;
        var first = SqlServerDatabase.AddPosition(p.PilotConnection, p.SmallContract,
            p.SmallSystem, null, null, p.Revision());
        PilotRunner.Check(first.Saved && first.PositionId.HasValue,
            "Public AddPosition failed");
        p.FirstPosition = first.PositionId.Value;
        var saved = SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
            "price", "100", p.Revision());
        PilotRunner.Check(saved.Saved, "First SaveCell failed");
        using var conn2 = p.Conn();
        PilotRunner.Check(SqlServerWorkspace.ReadContract(conn2, p.SmallContract).Id ==
            p.SmallContract, "Public contract reader cannot reopen saved contract");
    }

    private static void SeedAndRead(PilotRunner p, long system, int desired)
    {
        PilotRunner.Check(system > 0, "Workspace was not prepared");
        using var conn = p.Conn();
        var already = checked((int)PilotRunner.Long(conn,
            "SELECT COUNT_BIG(*) FROM dbo.positions WHERE system_id=@id", ("@id", system)));
        var needed = desired - already;
        PilotRunner.Check(needed > 0, "Unexpected fixture size");
        var data = new DataTable();
        data.Columns.Add("system_id", typeof(long));
        data.Columns.Add("name", typeof(string));
        data.Columns.Add("unit", typeof(string));
        data.Columns.Add("qty", typeof(double));
        data.Columns.Add("price", typeof(double));
        data.Columns.Add("purchase_price", typeof(double));
        data.Columns.Add("work_price", typeof(double));
        data.Columns.Add("order", typeof(int));
        for (var i = 0; i < needed; i++)
            data.Rows.Add(system, "Synthetic QA row " +
                i.ToString(CultureInfo.InvariantCulture), "шт", 2.0d, 100.0d,
                70.0d, 15.0d, already + i);
        var sw = Stopwatch.StartNew();
        using (var copy = new SqlBulkCopy(conn) {
            DestinationTableName = "dbo.positions", BatchSize = 1000, BulkCopyTimeout = 180
        })
        {
            foreach (DataColumn col in data.Columns)
                copy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            copy.WriteToServer(data);
        }
        sw.Stop();
        p.MetricOne("sql_bulk_fixture_insert", desired, sw.Elapsed.TotalMilliseconds,
            "Synthetic fixture ingestion, not a product API");
        PilotRunner.Check(PilotRunner.Long(conn,
            "SELECT COUNT_BIG(*) FROM dbo.positions WHERE system_id=@id", ("@id", system))
            == desired, "Lost rows during fixture load");

        var samples = new List<double>();
        for (var round = 0; round < 22; round++)
        {
            var timer = Stopwatch.StartNew();
            using var query = new SqlCommand(
                "SELECT id,name,qty,price FROM dbo.positions WHERE system_id=@id " +
                "ORDER BY [order],id", conn);
            query.Parameters.Add("@id", SqlDbType.BigInt).Value = system;
            using var reader = query.ExecuteReader();
            var count = 0;
            while (reader.Read())
            {
                _ = reader.GetInt64(0);
                _ = reader.GetString(1);
                _ = reader.GetDouble(2);
                _ = reader.GetDouble(3);
                count++;
            }
            timer.Stop();
            PilotRunner.Check(count == desired, "Incorrect SQL SELECT row count");
            if (round > 0) samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        p.MetricSamples("sql_read_positions", desired, samples,
            "21 warm-cache SQL reader samples; excludes WPF and EstimateReader.Load");
    }

    private static void Concurrency(PilotRunner p)
    {
        var revision = p.Revision();
        using var gate = new ManualResetEventSlim(false);
        var a = Task.Run(() => {
            gate.Wait();
            return SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
                "price", "311", revision);
        });
        var b = Task.Run(() => {
            gate.Wait();
            return SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
                "price", "322", revision);
        });
        gate.Set();
        Task.WaitAll(a, b);
        PilotRunner.Check(a.Result.Saved != b.Result.Saved,
            "Both writers committed or both writers failed");
        PilotRunner.Check(p.Revision() != revision, "No persisted revision after race");
        using var conn = p.Conn();
        var winner = a.Result.Saved ? 311.0d : 322.0d;
        PilotRunner.Check(PilotRunner.Double(conn,
            "SELECT price FROM dbo.positions WHERE id=@id", ("@id", p.FirstPosition))
            == winner, "Database does not contain winning client's price");
    }

    private static void SignedActs(PilotRunner p)
    {
        using (var conn = p.Conn())
        {
            var signed = PilotRunner.Long(conn,
                "INSERT dbo.acts(contract_id,number,signed) OUTPUT INSERTED.id " +
                "VALUES(@id,N'S',1)", ("@id", p.SmallContract));
            var unsigned = PilotRunner.Long(conn,
                "INSERT dbo.acts(contract_id,number,signed) OUTPUT INSERTED.id " +
                "VALUES(@id,N'U',0)", ("@id", p.SmallContract));
            foreach (var id in new[] { signed, unsigned })
                PilotRunner.Execute(conn,
                    "INSERT dbo.act_lines(act_id,position_id,qty,price,work_price) " +
                    "VALUES(@act,@pos,1,100,10)",
                    ("@act", id), ("@pos", p.FirstPosition));
        }
        var saved = SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
            "price", "450", p.Revision());
        PilotRunner.Check(saved.Saved, "Unlocked contract was not editable");
        using var check = p.Conn();
        var purchase = PilotRunner.Double(check,
            "SELECT ISNULL(purchase_price,-1) FROM dbo.positions WHERE id=@id",
            ("@id", p.FirstPosition));
        PilotRunner.Check(PilotRunner.Double(check,
            "SELECT price FROM dbo.act_lines WHERE position_id=@id AND " +
            "act_id IN (SELECT id FROM dbo.acts WHERE signed=1)",
            ("@id", p.FirstPosition)) == 100, "Signed act-line price overwritten");
        PilotRunner.Check(PilotRunner.Double(check,
            "SELECT price FROM dbo.act_lines WHERE position_id=@id AND " +
            "act_id IN (SELECT id FROM dbo.acts WHERE signed=0)",
            ("@id", p.FirstPosition)) == 450, "Unsigned act-line price not synced");
        PilotRunner.Execute(check, "UPDATE dbo.contracts SET locked=1 WHERE id=@id",
            ("@id", p.SmallContract));
        try
        {
            var revision = p.Revision();
            var rejected = SqlServerDatabase.SaveCell(p.PilotConnection,
                p.FirstPosition, "price", "999", revision);
            PilotRunner.Check(!rejected.Saved && p.Revision() == revision,
                "Locked contract allowed a revised price");
            PilotRunner.Check(PilotRunner.Double(check,
                "SELECT price FROM dbo.positions WHERE id=@id",
                ("@id", p.FirstPosition)) == 450, "Locked contract price was mutated");
            PilotRunner.Check(PilotRunner.Double(check,
                "SELECT ISNULL(purchase_price,-1) FROM dbo.positions WHERE id=@id",
                ("@id", p.FirstPosition)) == purchase,
                "Previously stored purchase price was mutated");
        }
        finally
        {
            PilotRunner.Execute(check, "UPDATE dbo.contracts SET locked=0 WHERE id=@id",
                ("@id", p.SmallContract));
        }
    }

    private static void Rollback(PilotRunner p)
    {
        using var conn = p.Conn();
        var oldRevision = p.Revision();
        var oldPrice = PilotRunner.Double(conn,
            "SELECT price FROM dbo.positions WHERE id=@id", ("@id", p.FirstPosition));
        var logs = PilotRunner.Long(conn, "SELECT COUNT_BIG(*) FROM dbo.change_log");
        try
        {
            PilotRunner.Execute(conn, "CREATE TRIGGER dbo.pilot_fail_log " +
                "ON dbo.change_log AFTER INSERT AS BEGIN " +
                "THROW 51031, 'Pilot fault after row and act-line updates', 1; END");
            var interrupted = false;
            try
            {
                _ = SqlServerDatabase.SaveCell(p.PilotConnection,
                    p.FirstPosition, "price", (oldPrice + 1).ToString("R", CultureInfo.InvariantCulture), oldRevision);
            }
            catch (SqlException ex) when (ex.Errors.Cast<SqlError>()
                .Any(x => x.Number == 51031)) { interrupted = true; }
            PilotRunner.Check(interrupted, "Fault injection was not caught by SQL API");
            PilotRunner.Check(p.Revision() == oldRevision,
                "meta.rev advanced despite transaction rollback");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.positions WHERE id=@id",
                ("@id", p.FirstPosition)) == oldPrice, "Position not rolled back");
            PilotRunner.Check(PilotRunner.Double(conn,
                "SELECT price FROM dbo.act_lines WHERE position_id=@id " +
                "AND act_id IN (SELECT id FROM dbo.acts WHERE signed=0)",
                ("@id", p.FirstPosition)) == oldPrice, "Act line not rolled back");
            PilotRunner.Check(PilotRunner.Long(conn,
                "SELECT COUNT_BIG(*) FROM dbo.change_log") == logs,
                "Partial change log survived rollback");
        }
        finally { PilotRunner.Execute(conn, "DROP TRIGGER IF EXISTS dbo.pilot_fail_log"); }
    }

    private static void UnknownCommit(PilotRunner p)
    {
        var before = p.Revision();
        var marker = "lost-ack-" + Guid.NewGuid().ToString("N");
        // This executes a real COMMIT but intentionally ignores its acknowledgement.
        // A physical network loss is a separately reported SKIP, not a passed scenario.
        _ = SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
            "note", marker, before);
        using var independentlyOpened = p.Conn();
        PilotRunner.Check(PilotRunner.Text(independentlyOpened,
            "SELECT note FROM dbo.positions WHERE id=@id", ("@id", p.FirstPosition))
            == marker, "Read-back failed to reconcile unknown COMMIT");
        PilotRunner.Check(p.Revision() != before, "No revision recorded after COMMIT");
        var ledgerEntries = PilotRunner.Long(independentlyOpened,
            "SELECT COUNT_BIG(*) FROM dbo.change_log WHERE entity=N'position' " +
            "AND entity_id=@id AND details LIKE @marker",
            ("@id", p.FirstPosition), ("@marker", "%" + marker + "%"));
        PilotRunner.Check(ledgerEntries == 1,
            "Missing/duplicate journal entry after COMMIT reconciliation");
        p.ReopenMarker = marker;
    }

    private static void Stage(PilotRunner p)
    {
        var stage = SqlServerStages.CreateStage(p.PilotConnection, p.SmallContract,
            p.Revision());
        using var conn = p.Conn();
        PilotRunner.Check(SqlServerStages.StagesOf(conn, p.SmallContract)
            .Any(x => x.Id == stage), "Public stage disappeared on connection reopen");
    }

    private static void StageLinkedPrices(PilotRunner p)
    {
        var cs = p.PilotConnection;
        var full = SqlServerWorkspace.CreateEmptyContract(cs, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-PRICES", "", "QA linked prices",
                null, null), p.Revision());
        var section = SqlServerDatabase.AddSection(cs, full, "Linked prices", false, null, p.Revision());
        PilotRunner.Check(section.Saved && section.SystemId.HasValue, "Could not create source section");
        var system = section.SystemId!.Value;
        long Position(string price)
        {
            var result = SqlServerDatabase.AddPosition(cs, full, system, null, null, p.Revision());
            PilotRunner.Check(result.Saved && result.PositionId.HasValue, "Could not create source position");
            var id = result.PositionId!.Value;
            foreach (var (field, value) in new[] { ("name", "Equal rows"), ("qty", "4"), ("price", price) })
                PilotRunner.Check(SqlServerDatabase.SaveCell(cs, id, field, value, p.Revision()).Saved,
                    "Could not seed source position");
            return id;
        }
        var first = Position("100");
        var second = Position("80");
        var open = SqlServerStages.CreateStage(cs, full, p.Revision());
        var signed = SqlServerStages.CreateStage(cs, full, p.Revision());
        foreach (var (stage, source) in new[] { (open, first), (open, second), (signed, second) })
            PilotRunner.Check(SqlServerStages.SetStageQty(cs, stage, source, 1, p.Revision()).Saved,
                "Could not allocate stage position");
        PilotRunner.Check(SqlServerWorkspace.SetContractLocked(cs, signed, true, p.Revision()),
            "Could not sign stage");
        var rest = SqlServerStages.CreateRest(cs, full, p.Revision());

        using var readback = p.Conn();
        decimal Price(long stage, long source) => Convert.ToDecimal(PilotRunner.Scalar(readback, """
            SELECT p.price FROM dbo.positions p JOIN dbo.systems s ON s.id=p.system_id
            WHERE s.contract_id=@stage AND p.stage_src=@source
            """, ("@stage", stage), ("@source", source)), CultureInfo.InvariantCulture);
        void AssertPrices(decimal expectedFirst, decimal expectedSecond)
        {
            PilotRunner.Check(Price(open, first) == expectedFirst && Price(rest, first) == expectedFirst &&
                Price(open, second) == expectedSecond && Price(rest, second) == expectedSecond &&
                Price(signed, second) == 80m, "Linked prices or signed snapshot differ on SQL readback");
        }

        PilotRunner.Check(SqlServerDatabase.SaveCell(cs, first, "price", "120", p.Revision()).Saved,
            "Editor price save failed");
        AssertPrices(120m, 80m);
        var copied = SqlServerPriceLike.SetPriceLike(cs, full, first, "price", p.Revision());
        PilotRunner.Check(copied.Saved && copied.Changed == 1, "Copy same price failed");
        AssertPrices(120m, 120m);
        PilotRunner.Check(SqlServerCostSummary.SaveSummaryCells(cs, checked((int)full),
            [(first, "130"), (second, "140")], "price", p.Revision()).Saved, "Summary price save failed");
        AssertPrices(130m, 140m);
        var previous = p.Revision();
        var pasted = SqlServerDatabase.SaveNumericPasteAtomic(cs, full, "price",
            [new(first, 150m), new(second, 160m)], previous);
        PilotRunner.Check(pasted.Saved && pasted.Changed == 2, "Numeric price paste failed");
        AssertPrices(150m, 160m);
        var stale = SqlServerDatabase.SaveNumericPasteAtomic(cs, full, "price", [new(first, 170m)], previous);
        PilotRunner.Check(!stale.Saved && p.Revision() == pasted.Revision, "Stale price paste changed revision");
        AssertPrices(150m, 160m);

        PilotRunner.Execute(readback, "INSERT dbo.acts(contract_id,number) VALUES(@rest,N'Legacy KS')",
            ("@rest", rest));
        PilotRunner.Check(SqlServerDatabase.SaveCell(cs, first, "price", "180", p.Revision()).Saved,
            "Editor price save failed with legacy KS attached to rest");
        PilotRunner.Check(Price(open, first) == 180m && Price(rest, first) == 150m,
            "A rest document with a legacy KS must keep its price");
    }

    private static void WorkPrices(PilotRunner p)
    {
        using var conn = p.Conn();
        // The test fixture prepares the existing documented 'pos' mode explicitly.
        // Product-facing writes still go exclusively through SetWorkPrice().
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET smr_mode=N'pos' WHERE id=@id",
            ("@id", p.LargeContract));
        var ids = new List<long>();
        using (var cmd = new SqlCommand(
            "SELECT TOP (120) id FROM dbo.positions WHERE system_id=@id ORDER BY id", conn))
        {
            cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = p.LargeSystem;
            using var r = cmd.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        PilotRunner.Check(ids.Count == 120, "Not enough rows to exercise batch");
        var timer = Stopwatch.StartNew();
        var result = SqlServerDatabase.SetWorkPrice(p.PilotConnection, p.LargeContract,
            ids, 48m, p.Revision());
        timer.Stop();
        PilotRunner.Check(result.Updated == ids.Count, "Partial SetWorkPrice success");
        PilotRunner.Check(PilotRunner.Long(conn,
            "SELECT COUNT_BIG(*) FROM dbo.positions WHERE system_id=@id AND work_price=48",
            ("@id", p.LargeSystem)) == ids.Count, "Bulk price not saved");
        p.MetricOne("public_set_work_price_batch", ids.Count, timer.Elapsed.TotalMilliseconds,
            "Actual transactional Core API");
    }

    private static void WriteLatencies(PilotRunner p)
    {
        var timings = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var timer = Stopwatch.StartNew();
            var result = SqlServerDatabase.SaveCell(p.PilotConnection, p.FirstPosition,
                "note", "perf-write-" + i.ToString(CultureInfo.InvariantCulture), p.Revision());
            timer.Stop();
            PilotRunner.Check(result.Saved, "Public SaveCell failed during benchmark");
            timings.Add(timer.Elapsed.TotalMilliseconds);
        }
        p.MetricSamples("public_save_cell", 1, timings,
            "20 writes with SQL round-trip + COMMIT + fresh connection");
    }
}
