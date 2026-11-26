using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class PilotFinanceBatchCases
{
    internal static void Run(PilotRunner p)
    {
        using var conn = SqlServerDatabase.Open(new SqlConnectionStringBuilder(p.PilotConnection)
            { MultipleActiveResultSets = false }.ConnectionString);
        p.RequireOwned(conn);
        var obj = SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties("Finance batch QA", "", "", "", "", ""), p.Revision());
        var id = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, obj,
            new SqlServerWorkspace.NewContractData("main", "QA-FIN", "", "Finance QA", null, null), p.Revision());
        var contractId = checked((int)id);
        var section = PilotRunner.Long(conn,
            "INSERT dbo.systems(contract_id,name,[order],heading,source_num) OUTPUT inserted.id " +
            "VALUES(@id,N'QA',0,N'',N'')", ("@id", id));

        long Position(string name, double qty, double price, double? plan = null,
            long? parent = null, bool outside = false, double? work = null) =>
            PilotRunner.Long(conn,
                "INSERT dbo.positions(system_id,parent_id,name,qty,price,purchase_price,out_of_kp,work_cost) " +
                "OUTPUT inserted.id VALUES(@sys,@parent,@name,@qty,@price,@plan,@outside,@work)",
                ("@sys", section), ("@parent", (object?)parent ?? DBNull.Value), ("@name", name),
                ("@qty", qty), ("@price", price), ("@plan", (object?)plan ?? DBNull.Value),
                ("@outside", outside), ("@work", (object?)work ?? DBNull.Value));
        void Actual(long position, double cost) => PilotRunner.Execute(conn,
            "INSERT dbo.procurement(position_id,cost_price) VALUES(@position,@cost)",
            ("@position", position), ("@cost", cost));
        var unit = Position("Unit", 2, 100, 30);
        Actual(unit, 40);
        _ = Position("Component", 3, 0, 2, unit);
        _ = Position("Outside", 4, 99, 5, outside: true);
        var work = Position("Монтаж установки", 2, 50, 20, work: 7);
        _ = Position("Work component", 5, 0, 2, work);
        var missing = Position("Missing price", 2, 10);
        var free = Position("Free purchase", 1, 25, 10);
        Actual(free, 0);
        PilotRunner.Execute(conn, "INSERT dbo.smr_costs(contract_id,amount) VALUES(@id,6)", ("@id", id));

        long Act(bool signed, bool exact) => PilotRunner.Long(conn,
            "INSERT dbo.acts(contract_id,signed,rows_exact) OUTPUT inserted.id VALUES(@id,@signed,@exact)",
            ("@id", id), ("@signed", signed), ("@exact", exact));
        void Line(long act, long position, double price) => PilotRunner.Execute(conn,
            "INSERT dbo.act_lines(act_id,position_id,qty,price) VALUES(@act,@position,1,@price)",
            ("@act", act), ("@position", position), ("@price", price));
        var signed = Act(true, false);
        Line(signed, unit, 12.5);
        var pending = Act(false, true);
        Line(pending, missing, 0.005);
        Line(pending, free, 0.005);

        var revision = SqlServerDatabase.Revision(conn);
        var scoped = FinanceReader.ReadSelected(conn, new[] { contractId, contractId }, out var totals);
        var expected = new ContractFinance(Signed: 12.5m, Pending: 0.01m, Cost: 86m,
            CoveredRevenue: 225m, OutsideCost: 20m, SmrCost: 20m, MissingPrices: 1)
            { MaterialRevenue = 245m };
        PilotRunner.Check(scoped.Count == 1 && scoped[contractId] == expected && totals.Count == 1,
            "Batched finance differs from independent fixture values");
        var acts = FinanceReader.ReadActs(conn).Where(a => a.ContractId == contractId).ToArray();
        PilotRunner.Check(acts.Length == 2 && acts.Where(a => a.Signed).Sum(a => a.Total) == expected.Signed &&
            acts.Where(a => !a.Signed).Sum(a => a.Total) == expected.Pending,
            "Batched finance and independent acts reader differ");

        var large = checked((int)p.LargeContract);
        var mixed = FinanceReader.ReadSelected(conn, new[] { large, contractId }, out var mixedTotals);
        var all = FinanceReader.ReadAll(conn);
        PilotRunner.Check(mixed.Count == 2 && mixedTotals.Count == 2 &&
            mixed[contractId] == expected && mixed[large] == all[large] && all[contractId] == expected,
            "Bounded mixed and unscoped SQL batches differ");
        var director = SqlServerDirector.DirectorRows(p.PilotConnection, "contracts").Single(d => d.ObjectId == obj);
        PilotRunner.Check(director.Total == totals.Single().Total && director.Closed == expected.Signed &&
            director.Revenue == expected.MaterialRevenue && director.Covered == expected.CoveredRevenue &&
            director.Cost == expected.Cost && director.OutCost == expected.OutsideCost &&
            director.Margin == 139m && director.SmrCost == expected.SmrCost &&
            director.Coverage == ContractFinance.CoveragePercent(245m, 225m),
            "Director aggregates changed after finance batching");
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == revision,
            "Read-only finance batch changed revision or retained an active reader");

        CheckLargeActs(p, conn, id, unit);
    }

    private static void CheckLargeActs(PilotRunner p, SqlConnection conn, long contractId, long position)
    {
        p.RequireOwned(conn);
        var exact = PilotRunner.Long(conn,
            "INSERT dbo.acts(contract_id,signed,rows_exact) OUTPUT inserted.id VALUES(@id,1,1)",
            ("@id", contractId));
        var rounded = PilotRunner.Long(conn,
            "INSERT dbo.acts(contract_id,signed,rows_exact) OUTPUT inserted.id VALUES(@id,0,0)",
            ("@id", contractId));
        var empty = PilotRunner.Long(conn,
            "INSERT dbo.acts(contract_id,signed,rows_exact) OUTPUT inserted.id VALUES(@id,0,0)",
            ("@id", contractId));
        // Synthetic repeated frozen lines stress the financial reader, not the editor.
        const string digits = "(VALUES(0),(1),(2),(3),(4),(5),(6),(7),(8),(9))";
        PilotRunner.Execute(conn, "INSERT dbo.act_lines(act_id,position_id,qty,price) " +
            "SELECT TOP (25000) @act,@position,1,0.125 FROM " + digits + " a(n) CROSS JOIN " +
            digits + " b(n) CROSS JOIN " + digits + " c(n) CROSS JOIN " + digits +
            " d(n) CROSS JOIN " + digits + " e(n)", ("@act", exact), ("@position", position));
        PilotRunner.Execute(conn, "INSERT dbo.act_lines(act_id,position_id,qty,price) " +
            "VALUES(@act,@position,1,0.125),(@act,@position,1,0.125),(@act,@position,-1,0.125)",
            ("@act", rounded), ("@position", position));
        PilotRunner.Check(PilotRunner.Long(conn, "SELECT COUNT_BIG(*) FROM dbo.act_lines WHERE act_id=@id",
            ("@id", exact)) == 25000, "Large frozen-act fixture incomplete");
        PilotRunner.Execute(conn, "INSERT dbo.tags([key],name,rules) VALUES(N'qa_fin_stream',N'QA',@rules)",
            ("@rules", "{\"round_rows\":true}"));

        foreach (var form in new[] { "", "qa_fin_stream" })
        {
            PilotRunner.Execute(conn, "UPDATE dbo.contracts SET form=@form WHERE id=@id",
                ("@form", form), ("@id", contractId));
            var revision = SqlServerDatabase.Revision(conn);
            var summaries = FinanceReader.ReadActs(conn).Where(a => a.ContractId == contractId).ToArray();
            PilotRunner.Check(summaries.Select(a => a.Id).SequenceEqual(summaries.Select(a => a.Id).OrderBy(id => id)),
                "Act header order changed");
            PilotRunner.Check(summaries.Single(a => a.Id == exact).Materials == 3125m &&
                summaries.Single(a => a.Id == rounded).Materials == (form.Length == 0 ? 0.12m : 0.13m) &&
                summaries.Single(a => a.Id == empty).Materials == 0m,
                "Large/exact/rounded/empty act material sums changed");
            var finance = FinanceReader.ReadSelected(conn, new[] { checked((int)contractId) }, out _)
                [checked((int)contractId)];
            PilotRunner.Check(finance.Signed == summaries.Where(a => a.Signed).Sum(a => a.Total) &&
                finance.Pending == summaries.Where(a => !a.Signed).Sum(a => a.Total),
                "Streamed act summaries and finance totals diverged");
            PilotRunner.Check(SqlServerDatabase.Revision(conn) == revision, "Act reads changed revision");
        }
    }
}
