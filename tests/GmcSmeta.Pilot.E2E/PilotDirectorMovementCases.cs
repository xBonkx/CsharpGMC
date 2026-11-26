using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

internal static class PilotDirectorMovementCases
{
    internal static void Run(PilotRunner p)
    {
        using var conn = SqlServerDatabase.Open(new SqlConnectionStringBuilder(p.PilotConnection)
            { MultipleActiveResultSets = false }.ConnectionString);
        p.RequireOwned(conn);
        long Object(string name) => SqlServerWorkspace.CreateObject(p.PilotConnection,
            new ObjectProperties(name, "", "", "", "", ""), p.Revision());
        var obj = Object("Director movement QA");
        long Contract(long owner, string kind, string number, long? parent = null) =>
            SqlServerWorkspace.CreateEmptyContract(p.PilotConnection, owner,
                new SqlServerWorkspace.NewContractData(kind, number, "", number, null, parent), p.Revision());
        long System(long contract) => PilotRunner.Long(conn,
            "INSERT dbo.systems(contract_id,name,[order],heading,source_num) OUTPUT inserted.id " +
            "VALUES(@id,N'QA',0,N'',N'')", ("@id", contract));
        long Position(long system, string name, double qty, double price,
            bool outside = false, long? parent = null) => PilotRunner.Long(conn,
            "INSERT dbo.positions(system_id,parent_id,name,qty,price,out_of_kp) " +
            "OUTPUT inserted.id VALUES(@sys,@parent,@name,@qty,@price,@outside)",
            ("@sys", system), ("@parent", (object?)parent ?? DBNull.Value), ("@name", name),
            ("@qty", qty), ("@price", price), ("@outside", outside));
        void Move(long position, string kind, double qty) => PilotRunner.Execute(conn,
            "INSERT dbo.movements(position_id,kind,qty) VALUES(@id,@kind,@qty)",
            ("@id", position), ("@kind", kind), ("@qty", qty));
        void Due(long position, string stage = "ordered", int offset = -2) => PilotRunner.Execute(conn,
            "INSERT dbo.procurement(position_id,stage,due_date) VALUES(@id,@stage,@due)",
            ("@id", position), ("@stage", stage), ("@due", DateTime.Today.AddDays(offset)));

        var main = Contract(obj, "main", "QA-MOVE-MAIN");
        var section = System(main);
        var issued = Position(section, "Unit", 10, 100);
        Move(issued, "out", 8); Move(issued, "back", 3); Due(issued);
        var missing = Position(section, "Missing", 2, 50); Due(missing);
        var returned = Position(section, "Supplier return", 4, 25);
        Move(returned, "in", 5); Move(returned, "back_supplier", 5); Due(returned);
        var partly = Position(section, "Part receipt", 3, 20);
        Move(partly, "in", 2); Move(partly, "back_supplier", 1); Due(partly);
        var over = Position(section, "Over issue", 1, 10); Move(over, "out", 2);
        var negative = Position(section, "Negative net issue", 2, 10);
        Move(negative, "out", 1); Move(negative, "back", 2);
        var work = Position(section, "Монтаж установки", 1, 200); Due(work);
        var child = Position(section, "Component", 1, 500, parent: issued); Due(child); Move(child, "out", 1);
        var outside = Position(section, "Outside", 1, 900, outside: true); Due(outside); Move(outside, "out", 1);

        var ds = Contract(obj, "dop", "QA-MOVE-DS", main);
        var dsPosition = Position(System(ds), "DS", 1, 40);
        Move(dsPosition, "out", 1);

        var old = Contract(obj, "kp", "QA-MOVE-OLD");
        _ = Position(System(old), "Old KP", 1, 9999);
        var latest = Contract(obj, "kp", "QA-MOVE-LATEST");
        var kp = Position(System(latest), "KP", 10, 100);
        Move(kp, "out", 6); Move(kp, "back", 1);
        Move(kp, "in", 7); Move(kp, "back_supplier", 2); Due(kp);
        var stage = Contract(obj, "kp", "QA-MOVE-STAGE");
        _ = Position(System(stage), "Stage KP", 1, 99999);
        PilotRunner.Execute(conn, "UPDATE dbo.contracts SET [order]=10 WHERE id=@old;" +
            "UPDATE dbo.contracts SET [order]=20 WHERE id=@latest;" +
            "UPDATE dbo.contracts SET [order]=30,stage_of=@latest WHERE id=@stage",
            ("@old", old), ("@latest", latest), ("@stage", stage));
        var archived = Object("Director movement archived QA");
        _ = Position(System(Contract(archived, "kp", "QA-MOVE-ARCH")), "Archived", 1, 100);
        PilotRunner.Execute(conn, "UPDATE dbo.objects SET archived=1 WHERE id=@id", ("@id", archived));

        var revision = SqlServerDatabase.Revision(conn);
        var contracts = SqlServerDirector.DirectorRows(p.PilotConnection, "contracts");
        var row = contracts.Single(x => x.ObjectId == obj);
        // 1290 main material + 40 DS; issue 500 + 10 - 10 + 40.
        // Late: missing, fully returned receipt, and work row; partial receipts,
        // children and outside-KP rows must not count.
        PilotRunner.Check(!row.KpOnly && row.Progress == 540m / 1330m * 100m && row.Late == 3,
            "Contract progress, late deliveries or DS scope changed");
        var kpRows = SqlServerDirector.DirectorRows(p.PilotConnection, "kp");
        var kpRow = kpRows.Single(x => x.ObjectId == obj);
        PilotRunner.Check(kpRow.KpOnly && kpRow.Progress == 50m && kpRow.Late == 0 &&
            kpRow.Total == Totals.ReadOne(conn, checked((int)latest))!.Total,
            "KP progress or latest non-stage scope changed");
        PilotRunner.Check(!contracts.Any(x => x.ObjectId == archived) &&
            !kpRows.Any(x => x.ObjectId == archived), "Archived object leaked into director scope");
        PilotRunner.Check(SqlServerDatabase.Revision(conn) == revision, "Director read changed revision");
    }
}
