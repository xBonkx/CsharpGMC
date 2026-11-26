using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

internal static class PilotStreamedTotalsCases
{
    internal static void Run(PilotRunner p)
    {
        using var conn = p.Conn();
        p.RequireOwned(conn);
        var obj = SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties("Streamed totals QA", "", "", "", "", ""), p.Revision());
        var id = SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, obj,
            new SqlServerWorkspace.NewContractData("kp", "QA-STREAM", "", "Stream QA",
                null, null), p.Revision());
        var contractId = checked((int)id);
        PilotRunner.Execute(conn, "INSERT dbo.tags([key],name,rules) VALUES(N'qa_stream',N'QA',@rules)",
            ("@rules", "{\"round_rows\":true,\"vat_top\":true}"));
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET form=N'qa_stream',vat_rate=22," +
            "smr_amount=12.34,smr_percent=13.25,transport_amount=5,pnr_amount=7 WHERE id=@id", ("@id", id));

        long Section(int order, decimal amount) => PilotRunner.Long(conn,
            "INSERT dbo.systems(contract_id,name,[order],smr_amount,heading,source_num) OUTPUT inserted.id " +
            "VALUES(@id,N'QA',@order,@amount,N'',N'')",
            ("@id", id), ("@order", order), ("@amount", amount));
        var s1 = Section(3, 10.125m);
        var s2 = Section(1, 2.345m);
        _ = Section(2, 1m); // empty section with SMR
        long Position(long section, int order, decimal qty, decimal price, decimal work,
            bool outside = false, long? parent = null) => PilotRunner.Long(conn,
            "INSERT dbo.positions(system_id,parent_id,name,[order],qty,price,work_price,out_of_kp) " +
            "OUTPUT inserted.id VALUES(@sys,@parent,N'QA',@order,@qty,@price,@work,@outside)",
            ("@sys", section), ("@parent", (object?)parent ?? DBNull.Value), ("@order", order),
            ("@qty", qty), ("@price", price), ("@work", work), ("@outside", outside));
        var top = Position(s1, 2, 1, 1.005m, 4.005m);
        _ = Position(s1, 1, 1, 1.005m, 2.005m);
        _ = Position(s1, 3, 1, 999, 999, outside: true);
        _ = Position(s1, 0, 1, 999, 999, parent: top);
        _ = Position(s2, 0, -1, 0.005m, 0.015m);
        _ = Position(s2, 0, 1, 0.005m, 0.015m);

        foreach (var form in new[] { "qa_stream", "" })
        foreach (var mode in new[] { "sum", "percent", "systems", "pos" })
        {
            PilotRunner.Execute(conn, "UPDATE dbo.contracts SET form=@form,smr_mode=@mode WHERE id=@id",
                ("@form", form), ("@mode", mode), ("@id", id));
            var revision = SqlServerDatabase.Revision(conn);
            var reference = EstimateReader.Load(conn, contractId).Totals;
            var all = Totals.ReadAll(conn).Single(t => t.Id == contractId);
            var one = Totals.ReadOne(conn, contractId);
            PilotRunner.Check(all == reference && one == reference && all.Positions == 6,
                $"Streamed totals differ for {form}/{mode}");
            PilotRunner.Check(all.Materials == (form.Length > 0 ? 2.02m : 2.01m),
                "Material row rounding or exclusion changed");
            PilotRunner.Check(SqlServerDatabase.Revision(conn) == revision,
                "Totals read changed SQL revision");
        }

        var large = Totals.ReadAll(conn).ToDictionary(t => t.Id);
        foreach (var key in new[] { p.SmallContract, p.LargeContract })
        {
            var reference = EstimateReader.Load(conn, checked((int)key)).Totals;
            PilotRunner.Check(large[reference.Id] == reference &&
                Totals.ReadOne(conn, reference.Id) == reference,
                "Streamed totals differ on large fixture");
        }
    }
}
