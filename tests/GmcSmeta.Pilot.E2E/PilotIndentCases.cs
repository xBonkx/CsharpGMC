using GmcSmeta.Core.Db;

internal static class PilotIndentCases
{
    internal static void Run(PilotRunner p)
    {
        var cs = p.PilotConnection;
        using var conn = p.Conn();
        p.RequireOwned(conn);
        var contract = SqlServerWorkspace.CreateEmptyContract(cs, p.ObjectId,
            new SqlServerWorkspace.NewContractData("kp", "QA-INDENT", "", "QA indent", null, null), p.Revision());
        var section = SqlServerDatabase.AddSection(cs, contract, "QA", false, null, p.Revision());
        PilotRunner.Check(section.Saved && section.SystemId.HasValue, "section fixture");
        var system = section.SystemId!.Value;
        long Position(long? after)
        {
            var result = SqlServerDatabase.AddPosition(cs, contract, system, after, null, p.Revision());
            PilotRunner.Check(result.Saved && result.PositionId.HasValue, "position fixture");
            var id = result.PositionId!.Value;
            PilotRunner.Check(SqlServerDatabase.SaveCell(cs, id, "qty", "3", p.Revision()).Saved, "quantity fixture");
            PilotRunner.Check(SqlServerDatabase.SaveCell(cs, id, "price", "75", p.Revision()).Saved, "price fixture");
            return id;
        }
        var first = Position(null);
        var second = Position(first);
        var third = Position(second);
        string Values() => PilotRunner.Text(conn, @"
            SELECT id,system_id,qty,price,purchase_price,formulas FROM dbo.positions
            WHERE system_id=@s ORDER BY id FOR JSON PATH, INCLUDE_NULL_VALUES", ("@s", system));
        string Hierarchy() => PilotRunner.Text(conn, @"
            SELECT id,parent_id,[order] FROM dbo.positions WHERE system_id=@s ORDER BY [order],id
            FOR JSON PATH, INCLUDE_NULL_VALUES", ("@s", system));
        var values = Values();
        var original = Hierarchy();
        var revision = p.Revision();
        var nested = SqlServerDatabase.MakeSubposition(cs, contract, second, true, revision);
        PilotRunner.Check(nested.Saved && nested.Revision != revision && Values() == values, "indent changed financial fields");
        PilotRunner.Check(PilotRunner.Long(conn,"SELECT parent_id FROM dbo.positions WHERE id=@id",("@id",second)) == first, "wrong parent");
        var hierarchy = Hierarchy();
        foreach (var denied in new[]
        {
            SqlServerDatabase.MakeSubposition(cs, contract, second, false, revision),
            SqlServerDatabase.MakeSubposition(cs, p.SmallContract, third, true, nested.Revision),
            SqlServerDatabase.MakeSubposition(cs, contract, first, true, nested.Revision)
        })
            PilotRunner.Check(!denied.Saved, "unsafe indent accepted");
        PilotRunner.Check(Hierarchy() == hierarchy && p.Revision() == nested.Revision && Values() == values,
            "refused operation changed hierarchy, financial fields or revision");
        var flat = SqlServerDatabase.MakeSubposition(cs, contract, second, false, nested.Revision);
        PilotRunner.Check(flat.Saved && Hierarchy() == original && Values() == values, "outdent failed round-trip");
    }
}
