using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

// Own isolated SQL Server Pilot fixture; setup only, public reader for assertions.
internal static class PilotCostSummaryCases
{
    internal static void Run(PilotRunner p)
    {
        var obj = SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties("Cost summary QA", "", "", "", "", ""), p.Revision());
        var contract = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, obj,
            new SqlServerWorkspace.NewContractData("kp", "QA-COST-BATCH", "", "Cost QA",
                null, null), p.Revision());
        using var conn = p.Conn();
        p.RequireOwned(conn);
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET transport_amount=25,transport_cost=NULL WHERE id=@id",
            ("@id", contract));

        long Section(string name, int order) => PilotRunner.Long(conn,
            "INSERT dbo.systems(contract_id,name,[order],heading,source_num) OUTPUT inserted.id " +
            "VALUES(@doc,@name,@order,N'',N'')",
            ("@doc", contract), ("@name", name), ("@order", order));
        var empty = Section("Пустая", 0);
        var system = Section("П1", 1);

        long Position(string name, int order, int qty, int price, int? plan, long? parent = null,
            bool outside = false) => PilotRunner.Long(conn,
            "INSERT dbo.positions(system_id,parent_id,name,[order],qty,price,purchase_price,out_of_kp) " +
            "OUTPUT inserted.id VALUES(@sys,@parent,@name,@order,@qty,@price,@plan,@outside)",
            ("@sys", system), ("@parent", (object?)parent ?? DBNull.Value),
            ("@name", name), ("@order", order), ("@qty", qty), ("@price", price),
            ("@plan", (object?)plan ?? DBNull.Value), ("@outside", outside));
        var material = Position("Установка", 1, 2, 100, 50);
        var zero = Position("Нулевой факт", 2, 1, 20, 99);
        var missing = Position("Без закупа", 3, 1, 30, null);
        var work = Position("Монтаж П1", 4, 3, 20, null);
        _ = Position("Комплектующее", 0, 3, 0, 7, material);
        _ = Position("Вне КП", 5, 2, 0, 8, outside: true);
        PilotRunner.Execute(conn, "INSERT dbo.procurement(position_id,cost_price) VALUES(@id,0)", ("@id", zero));
        PilotRunner.Execute(conn, "UPDATE dbo.positions SET work_cost=5,work_sell=10 WHERE id=@id", ("@id", work));
        PilotRunner.Execute(conn, "INSERT dbo.smr_costs(contract_id,amount) VALUES(@id,11),(@id,12)", ("@id", contract));

        var before = SqlServerDatabase.Revision(conn);
        var data = SqlServerCostSummary.Summary(conn, checked((int)contract));
        PilotRunner.Check(!string.IsNullOrEmpty(before) && SqlServerDatabase.Revision(conn) == before,
            "Cost summary read must not change revision");
        PilotRunner.Check(data.Systems.Select(s => s.Id).SequenceEqual(new[] { empty, system }) &&
            data.Rows.Select(row => row.Id).SequenceEqual(new[] { material, zero, missing, work }),
            "Cost batch lost empty systems, position order or included children/extra twice");
        var first = data.Rows[0];
        PilotRunner.Check(first.MCost == 121m && first.MSell == 200m && first.Kids &&
            data.Rows[1].UCost == 0m && data.Rows[1].Fact && !data.Rows[1].NoCost &&
            data.Rows[2].UCost is null && data.Rows[2].NoCost &&
            data.Rows[3].Work && data.Rows[3].MSell == 0m &&
            data.Rows[3].WCost == 15m && data.Rows[3].WSell == 30m,
            "Component allocation, actual zero, missing price or work projection changed");
        // Compare independent scalar SQL and existing totals reader after the
        // batch has released its reader; the connection need not enable MARS.
        var expenses = PilotRunner.Double(conn,
            "SELECT ISNULL(SUM(amount),0) FROM dbo.smr_costs WHERE contract_id=@id", ("@id", contract));
        var transport = PilotRunner.Double(conn,
            "SELECT transport_amount FROM dbo.contracts WHERE id=@id", ("@id", contract));
        PilotRunner.Check(data.Extra.SequenceEqual(new[]
        {
            ("Прочие затраты на монтаж", (decimal)expenses, 0m, "works"),
            ("Куплено вне КП", 16m, 0m, "mat"),
            ("Транспорт", 0m, (decimal)transport, "trans")
        }) && data.Notes.SequenceEqual(new[] { "наш расход на транспорт не вписан" }) &&
            data.SmrContract == (Totals.ReadOne(conn, checked((int)contract))?.Smr ?? 0m) &&
            data.TotalCost == 175m && data.TotalSell == 305m,
            "Extra order, null transport, SMR or monetary totals changed");
        var contractId = checked((int)contract);
        using var readCancellation = new CancellationTokenSource();
        var cancellable = SqlServerCostSummary.Summary(conn, contractId, readCancellation.Token);
        PilotRunner.Check(cancellable.Rows.SequenceEqual(data.Rows) &&
            cancellable.Systems.SequenceEqual(data.Systems) && cancellable.Extra.SequenceEqual(data.Extra) &&
            cancellable.Notes.SequenceEqual(data.Notes) && cancellable.SmrContract == data.SmrContract &&
            SqlServerDatabase.Revision(conn) == before,
            "Cancellable summary changed the read-only projection");
        readCancellation.Cancel();
        try
        {
            SqlServerCostSummary.Summary(conn, contractId, readCancellation.Token);
            throw new InvalidOperationException("Cancelled summary read did not throw.");
        }
        catch (OperationCanceledException) { }
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == before, "Cancelled read changed revision");
        var legacyMaterials = SqlServerCostSummary.MaterialsData(p.PilotConnection, contractId);
        using var materialCancellation = new CancellationTokenSource();
        var materials = SqlServerCostSummary.MaterialsData(conn, contractId, materialCancellation.Token);
        PilotRunner.Check(materials.Systems.SequenceEqual(legacyMaterials.Systems) &&
            materials.Positions.SequenceEqual(legacyMaterials.Positions) &&
            (materials with { Systems = legacyMaterials.Systems, Positions = legacyMaterials.Positions }) == legacyMaterials &&
            materials.Positions.Select(row => row.Id).Contains(material) &&
            SqlServerDatabase.Revision(conn) == before,
            "Same-connection materials read changed projection or revision");
        materialCancellation.Cancel();
        try
        {
            SqlServerCostSummary.MaterialsData(conn, contractId, materialCancellation.Token);
            throw new InvalidOperationException("Cancelled materials read did not throw.");
        }
        catch (OperationCanceledException) { }
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == before, "Cancelled materials read changed revision");
        var standaloneTotals = SqlServerCostTotals.Totals(conn, contractId, data);
        var combined = SqlServerCostTotals.SummaryAndTotals(conn, contractId);
        PilotRunner.Check(combined.Summary.Rows.SequenceEqual(data.Rows) &&
            combined.Summary.Extra.SequenceEqual(data.Extra) &&
            combined.Totals.Lines.SequenceEqual(standaloneTotals.Lines) &&
            (combined.Totals with { Lines = standaloneTotals.Lines }) == standaloneTotals,
            "Shared document totals/rules changed cost projection");
        var hit = CostFinancialSnapshot.ReadTotals(conn, contractId, data, before);
        var miss = CostFinancialSnapshot.ReadTotals(conn, contractId, data, "stale");
        var emptyRevision = CostFinancialSnapshot.ReadTotals(conn, contractId, data, "");
        PilotRunner.Check(ReferenceEquals(hit.Summary, data) && hit.Revision == before &&
            !ReferenceEquals(miss.Summary, data) && !ReferenceEquals(emptyRevision.Summary, data) &&
            hit.Totals.Lines.SequenceEqual(miss.Totals.Lines) &&
            (hit.Totals with { Lines = miss.Totals.Lines }) == miss.Totals &&
            miss.Summary.Rows.SequenceEqual(data.Rows) && emptyRevision.Summary.Rows.SequenceEqual(data.Rows),
            "Revision-fenced summary reuse accepted stale cache or changed money");
        using var totalsCancellation = new CancellationTokenSource();
        var asyncHit = CostFinancialSnapshot.ReadTotalsAsync(conn, contractId, data, before, totalsCancellation.Token)
            .GetAwaiter().GetResult();
        var asyncMiss = CostFinancialSnapshot.ReadTotalsAsync(conn, contractId, data, "stale", totalsCancellation.Token)
            .GetAwaiter().GetResult();
        PilotRunner.Check(ReferenceEquals(asyncHit.Summary, data) && asyncHit.Revision == before &&
            !ReferenceEquals(asyncMiss.Summary, data) && asyncMiss.Revision == before &&
            asyncMiss.Summary.Rows.SequenceEqual(data.Rows) &&
            asyncMiss.Summary.Extra.SequenceEqual(data.Extra) &&
            asyncHit.Totals.Lines.SequenceEqual(hit.Totals.Lines) &&
            asyncMiss.Totals.Lines.SequenceEqual(miss.Totals.Lines) &&
            (asyncHit.Totals with { Lines = hit.Totals.Lines }) == hit.Totals &&
            (asyncMiss.Totals with { Lines = miss.Totals.Lines }) == miss.Totals,
            "Cancellable totals cache hit/miss changed the monetary projection");
        totalsCancellation.Cancel();
        try
        {
            CostFinancialSnapshot.ReadTotalsAsync(conn, contractId, data, before, totalsCancellation.Token)
                .GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancelled totals read did not throw.");
        }
        catch (OperationCanceledException) { }
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == before, "Cancelled totals read changed revision");
        var missingDoc = SqlServerCostSummary.Summary(conn, int.MaxValue);
        PilotRunner.Check(missingDoc.Rows.Count == 0 && missingDoc.Systems.Count == 0 &&
            missingDoc.Extra.SequenceEqual(new[] { ("Транспорт", 0m, 0m, "trans") }) &&
            missingDoc.SmrContract == 0m,
            "Empty result sets must not shift subsequent result sets");
    }
}
