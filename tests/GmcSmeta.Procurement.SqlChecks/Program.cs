using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

internal static class Program
{
    private static int checks;
    private static readonly string PasswordSafeDatabasePrefix = "GmcSmetaPilot_ProcurementCI_";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("PROCUREMENT SQL: " + message);
        checks++;
    }

    private static void Execute(string connection, string sql)
    {
        using var conn = new SqlConnection(connection);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(string connection, string sql)
    {
        using var conn = new SqlConnection(connection);
        conn.Open();
        using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        return cmd.ExecuteScalar();
    }

    private static long Count(string connection, string sql) =>
        Convert.ToInt64(Scalar(connection, sql) ?? 0, CultureInfo.InvariantCulture);

    private static string Revision(string connection) =>
        Convert.ToString(Scalar(connection, "SELECT [value] FROM dbo.meta WHERE [key]=N'rev'"),
            CultureInfo.InvariantCulture) ?? throw new InvalidOperationException("No meta.rev");

    private static void AssertCannotSave(SqlServerDatabase.SaveResult result, string details) =>
        Check(!result.Saved, details);

    private static void Schema(string cs)
    {
        Execute(cs, @"
CREATE TABLE dbo.meta ([key] NVARCHAR(128) NOT NULL PRIMARY KEY,[value] NVARCHAR(300) NOT NULL);
INSERT dbo.meta([key],[value]) VALUES(N'rev',N'test-first');
CREATE TABLE dbo.objects (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name NVARCHAR(255) NOT NULL,
    address NVARCHAR(255) NOT NULL DEFAULT N'',
    archived BIT NOT NULL DEFAULT 0);
INSERT dbo.objects(name,address) VALUES(N'Объект вентиляция',N'Тестовый адрес');
CREATE TABLE dbo.companies (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name NVARCHAR(255) NOT NULL,
    legal_full NVARCHAR(MAX) NOT NULL DEFAULT N'');
CREATE TABLE dbo.contracts (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    object_id BIGINT NOT NULL,
    kind NVARCHAR(16) NOT NULL,
    name NVARCHAR(255) NOT NULL,
    ks_site_part NVARCHAR(MAX) NOT NULL DEFAULT N'',
    number NVARCHAR(64) NOT NULL DEFAULT N'',
    [date] NVARCHAR(32) NOT NULL DEFAULT N'',
    vat_rate FLOAT NOT NULL DEFAULT 22,
    smr_amount FLOAT NOT NULL DEFAULT 0,
    transport_amount FLOAT NOT NULL DEFAULT 0,
    source_file NVARCHAR(MAX) NOT NULL DEFAULT N'',
    source_sheet NVARCHAR(MAX) NOT NULL DEFAULT N'',
    [order] INT NOT NULL DEFAULT 0,
    created_at DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    company_id BIGINT NULL,
    base_contract_id BIGINT NULL,
    smr_mode NVARCHAR(16) NOT NULL DEFAULT N'sum',
    smr_percent FLOAT NOT NULL DEFAULT 0,
    note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    kp_phase NVARCHAR(30) NOT NULL DEFAULT N'',
    stage_rest BIT NOT NULL DEFAULT 0,
    form NVARCHAR(32) NOT NULL DEFAULT N'',
    m2_mode NVARCHAR(16) NOT NULL DEFAULT N'',
    pnr_amount FLOAT NOT NULL DEFAULT 0,
    transport_note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    locked BIT NOT NULL DEFAULT 0,
    locked_by NVARCHAR(255) NOT NULL DEFAULT N'');
INSERT dbo.contracts(object_id,kind,name,[order]) VALUES
    (1,N'main',N'Тестовый договор',1),
    (1,N'main',N'Чужой тестовый договор',2);
CREATE TABLE dbo.systems (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    contract_id BIGINT NOT NULL,
    name NVARCHAR(255) NOT NULL,
    [order] INT NOT NULL DEFAULT 0,
    parent_id BIGINT NULL,
    smr_amount FLOAT NULL,
    heading NVARCHAR(MAX) NOT NULL DEFAULT N'',
    source_num NVARCHAR(255) NOT NULL DEFAULT N'',
    stage_sys_src NVARCHAR(255) NULL);
INSERT dbo.systems(contract_id,name,[order]) VALUES
    (1,N'ОВ-1',1),(2,N'Чужая система',1);
CREATE TABLE dbo.positions (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    system_id BIGINT NOT NULL,
    parent_id BIGINT NULL,
    catalog_item_id BIGINT NULL,
    num NVARCHAR(64) NOT NULL DEFAULT N'',
    basis NVARCHAR(MAX) NOT NULL DEFAULT N'',
    name NVARCHAR(255) NOT NULL DEFAULT N'',
    unit NVARCHAR(32) NOT NULL DEFAULT N'шт',
    qty FLOAT NOT NULL DEFAULT 1,
    price FLOAT NOT NULL DEFAULT 0,
    purchase_price FLOAT NULL,
    note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    extra_json NVARCHAR(MAX) NOT NULL DEFAULT N'',
    source_row INT NULL,
    [order] INT NOT NULL DEFAULT 0,
    out_of_kp BIT NOT NULL DEFAULT 0,
    formulas NVARCHAR(MAX) NOT NULL DEFAULT N'',
    stage_src NVARCHAR(255) NULL,
    area_manual FLOAT NULL,
    work_price FLOAT NULL,
    cost_note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    cost_color NVARCHAR(32) NOT NULL DEFAULT N'',
    work_sell FLOAT NULL,
    work_cost FLOAT NULL);
INSERT dbo.positions(system_id,name,unit,qty,[order]) VALUES
    (1,N'Воздуховод',N'м²',10,0),
    (1,N'Отвод',N'шт',5,1),
    (1,N'Тройник',N'шт',3,2),
    (2,N'Чужая позиция',N'шт',1,0);
CREATE TABLE dbo.suppliers (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,name NVARCHAR(255) NOT NULL);
CREATE TABLE dbo.procurement (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    position_id BIGINT NOT NULL UNIQUE,
    supplier_id BIGINT NULL,
    stage NVARCHAR(32) NOT NULL DEFAULT N'',
    order_ref NVARCHAR(255) NOT NULL DEFAULT N'',
    invoice_ref NVARCHAR(255) NOT NULL DEFAULT N'',
    paid_date DATE NULL,
    cost_price FLOAT NULL,
    due_date DATE NULL,
    replacement NVARCHAR(MAX) NOT NULL DEFAULT N'',
    note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    direct_to_site BIT NOT NULL DEFAULT 0,
    source NVARCHAR(12) NOT NULL DEFAULT N'buy',
    updated_at DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    updated_by NVARCHAR(255) NOT NULL DEFAULT N'');
CREATE TABLE dbo.shipments (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    object_id BIGINT NOT NULL,
    number NVARCHAR(50) NOT NULL,
    [date] DATE NULL,
    recipient NVARCHAR(255) NOT NULL DEFAULT N'',
    issued_by NVARCHAR(255) NOT NULL DEFAULT N'',
    note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    fixed BIT NOT NULL DEFAULT 0,
    fixed_at DATETIME2 NULL,
    fixed_by NVARCHAR(255) NULL);
CREATE TABLE dbo.shipment_lines (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    shipment_id BIGINT NOT NULL,position_id BIGINT NOT NULL,qty FLOAT NOT NULL);
CREATE TABLE dbo.movements (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    position_id BIGINT NOT NULL,
    kind NVARCHAR(30) NOT NULL,
    qty FLOAT NOT NULL,
    [date] DATE NULL,
    doc NVARCHAR(MAX) NOT NULL DEFAULT N'',
    note NVARCHAR(MAX) NOT NULL DEFAULT N'',
    [user] NVARCHAR(255) NOT NULL DEFAULT N'',
    shipment_id BIGINT NULL);
CREATE TABLE dbo.change_log (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    ts DATETIME2 NOT NULL,
    [user] NVARCHAR(255) NOT NULL,
    entity NVARCHAR(40) NOT NULL,
    entity_id BIGINT NOT NULL,
    action NVARCHAR(40) NOT NULL,
    details NVARCHAR(MAX) NOT NULL DEFAULT N'',
    contract_id BIGINT NULL,
    object_id BIGINT NULL);
CREATE TABLE dbo.act_lines (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    position_id BIGINT NOT NULL);
CREATE TABLE dbo.catalog_items (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    [key] NVARCHAR(600) NOT NULL,
    name NVARCHAR(255) NOT NULL,
    unit NVARCHAR(32) NOT NULL);
CREATE TABLE dbo.price_history (
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    item_id BIGINT NOT NULL,contract_id BIGINT NULL,
    price FLOAT NOT NULL,purchase_price FLOAT NULL,
    [date] NVARCHAR(32) NOT NULL DEFAULT N'',
    created_at DATETIME2 NOT NULL DEFAULT SYSDATETIME());
");
    }

    private static void Run(string connection)
    {
        Schema(connection);
        var initial = Revision(connection);
        // Reject stale, unknown and invalid movements before touching any row.
        AssertCannotSave(SqlServerProcurement.AddMovements(connection, 1,
            new[] { new SqlServerProcurement.MovementInput(1, 0m) },
            "in", DateTime.Today, "", "", initial), "Zero quantity accepted");
        AssertCannotSave(SqlServerProcurement.AddMovements(connection, 1,
            new[] { new SqlServerProcurement.MovementInput(1, 1m) },
            "out", DateTime.Today, "", "", initial), "Outgoing without shipment accepted");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.movements") == 0,
            "Invalid movements wrote rows");

        var batch = new[]
        {
            new SqlServerProcurement.MovementInput(1, 2.5m),
            new SqlServerProcurement.MovementInput(2, 1m)
        };
        var move = SqlServerProcurement.AddMovements(connection, 1, batch, "in",
            new DateTime(2026, 11, 21), "UPD-1", "Приёмка", initial);
        Check(move.Saved && move.Revision != initial, "Two-position arrival batch failed");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.movements WHERE kind=N'in'") == 2,
            "Arrival count mismatch");
        AssertCannotSave(SqlServerProcurement.AddMovements(connection, 1, batch, "in",
            DateTime.Today, "UPD-1", "duplicate", initial), "Stale revision accepted");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.movements") == 2,
            "Stale batch duplicated a movement");
        var rev = Revision(connection);
        var invalidScope = SqlServerProcurement.AddMovements(connection, 1,
            new[]
            {
                new SqlServerProcurement.MovementInput(1, 9m),
                new SqlServerProcurement.MovementInput(4, 9m)
            }, "back", DateTime.Today, "", "", rev);
        Check(!invalidScope.Saved, "Cross-contract movement accepted");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.movements") == 2,
            "Cross-contract batch was partially committed");

        var originalId = Count(connection,
            "SELECT TOP(1) id FROM dbo.movements WHERE kind=N'in' AND position_id=1");
        var edited = SqlServerProcurement.EditMovement(connection, 1, 1, originalId,
            3.5m, new DateTime(2026, 11, 20), "UPD-2", "Изменено", Revision(connection));
        Check(edited.Saved, "Independent movement edit failed");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.movements WHERE id={originalId} AND qty=3.5") == 1,
            "Movement edit was not persisted");

        var shipment = SqlServerProcurement.IssueViaListBatch(connection, 1,
            new[]
            {
                new SqlServerProcurement.MovementInput(1, 1m),
                new SqlServerProcurement.MovementInput(2, 2m)
            }, new DateTime(2026, 11, 21), "Инженер", true, Revision(connection));
        Check(shipment.ShipmentId > 0, "Fixed shipment not generated");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.movements WHERE kind=N'out' AND shipment_id IS NOT NULL") == 2,
            "Shipments and movements not linked");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.shipment_lines") == 2,
            "Shipment lines missing");
        var outId = Count(connection,
            "SELECT TOP(1) id FROM dbo.movements WHERE kind=N'out' AND position_id=1");
        Execute(connection, $"UPDATE dbo.movements SET [user]=N'Первоначальный автор' WHERE id={outId}");
        var repeatedIssue = SqlServerProcurement.IssueViaListBatch(connection, 1,
            new[] { new SqlServerProcurement.MovementInput(1, 0.5m) },
            new DateTime(2026, 11, 21), "Инженер", true, Revision(connection));
        Check(repeatedIssue.ShipmentId == shipment.ShipmentId, "Repeated issue created a second list");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.movements WHERE id={outId} AND qty=1.5 AND [user]=N'Первоначальный автор'") == 1,
            "Repeated issue lost identity or author");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id={shipment.ShipmentId} AND position_id=1 AND qty=1.5") == 1,
            "Linked line not updated");
        Execute(connection, $"UPDATE dbo.movements SET qty=7 WHERE id={outId}");
        var corruptRevision = Revision(connection);
        var rejectedIssue = false;
        try
        {
            SqlServerProcurement.IssueViaListBatch(connection, 1,
                new[] { new SqlServerProcurement.MovementInput(2, 0.5m), new SqlServerProcurement.MovementInput(1, 0.5m) },
                new DateTime(2026, 11, 21), "Инженер", true, corruptRevision);
        }
        catch (InvalidOperationException) { rejectedIssue = true; }
        Check(rejectedIssue && Revision(connection) == corruptRevision, "Corrupt movement not rejected");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.shipment_lines WHERE shipment_id={shipment.ShipmentId} AND position_id=2 AND qty=2") == 1,
            "Batch was partially committed");
        Execute(connection, $"UPDATE dbo.movements SET qty=1.5 WHERE id={outId}");
        AssertCannotSave(SqlServerProcurement.EditMovement(connection, 1, 1, outId, 4m,
            DateTime.Today, "wrong", "", Revision(connection)),
            "Shipment-backed movement edited directly");
        AssertCannotSave(SqlServerProcurement.DeleteMovement(connection, 1, 1, outId,
            Revision(connection)), "Shipment-backed movement deleted directly");

        // A corrupt draft must never erase movements; simulate only in this disposable database.
        var draftId = Count(connection, "INSERT dbo.shipments(object_id,number,[date],fixed) OUTPUT INSERTED.id VALUES(1,N'QA-draft',CONVERT(date,GETDATE()),0)");
        var strayId = Count(connection, $"INSERT dbo.movements(position_id,kind,qty,shipment_id) OUTPUT INSERTED.id VALUES(1,N'out',1,{draftId})");
        var draftRev = Revision(connection);
        AssertCannotSave(SqlServerShipments.DeleteList(connection, draftId, draftRev), "Corrupt draft deleted");
        AssertCannotSave(SqlServerShipments.Unfix(connection, draftId, draftRev), "Draft unfix accepted");
        Check(Revision(connection) == draftRev &&
            Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.movements WHERE id={strayId}") == 1,
            "Draft operation removed movement or changed revision");
        Execute(connection, $"DELETE dbo.movements WHERE id={strayId}");
        AssertCannotSave(SqlServerShipments.DeleteList(connection, draftId, draftRev, 999999), "Wrong-object draft deleted");
        Check(Revision(connection) == draftRev &&
            Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.shipments WHERE id={draftId}") == 1,
            "Wrong-object delete modified the shipment or revision");
        Execute(connection, "UPDATE dbo.objects SET archived=1 WHERE id=1");
        AssertCannotSave(SqlServerShipments.DeleteList(connection, draftId, draftRev, 1), "Archived-object draft deleted");
        Check(Revision(connection) == draftRev &&
            Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.shipments WHERE id={draftId}") == 1,
            "Archived-object delete changed data");
        Execute(connection, "UPDATE dbo.objects SET archived=0 WHERE id=1");
        Check(SqlServerShipments.DeleteList(connection, draftId, draftRev, 1).Saved, "Clean draft deletion failed");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.change_log WHERE entity=N'shipment' AND entity_id={draftId} AND action=N'delete'") == 1,
            "Draft deletion missing audit");

        var extra = SqlServerProcurement.AddExtra(connection, 1, null,
            "Вне КП — вентилятор", "шт", 2m, 81m, Revision(connection));
        Check(extra.Saved && extra.Id > 0, "Extra purchase not created");
        var editExtra = SqlServerProcurement.EditExtra(connection, 1, extra.Id!.Value,
            "Докупленный вентилятор", "компл", 3m, Revision(connection));
        Check(editExtra.Saved, "Extra position edit failed");
        Check(Count(connection,
            $"SELECT COUNT_BIG(*) FROM dbo.positions WHERE id={extra.Id} AND out_of_kp=1 AND qty=3") == 1,
            "Extra changed real KP or lost out-of-KP flag");
        var second = SqlServerProcurement.AddExtra(connection, 1, null,
            "Дополнительная решётка", "шт", 3m, 9m, Revision(connection));
        Check(second.Saved && second.Id > 0, "Second extra not created");
        var keepMovement = SqlServerProcurement.AddMovements(connection, 1,
            new[] { new SqlServerProcurement.MovementInput(extra.Id!.Value, 1m) },
            "in", DateTime.Today, "накладная", "", Revision(connection));
        Check(keepMovement.Saved, "Extra arrival failed");
        // Malformed cross-contract child must not be relocated or produce a partial supplementary agreement.
        Execute(connection, $"UPDATE dbo.positions SET parent_id={extra.Id} WHERE id=4");
        var malformedRev = Revision(connection);
        var contractCount = Count(connection, "SELECT COUNT_BIG(*) FROM dbo.contracts");
        var blockedDop = SqlServerProcurement.CreateDopFromExtras(connection, 1, malformedRev);
        Check(!blockedDop.Saved && Revision(connection) == malformedRev,
            "Malformed hierarchy accepted");
        Check(Count(connection, "SELECT COUNT_BIG(*) FROM dbo.contracts") == contractCount &&
            Count(connection, "SELECT COUNT_BIG(*) FROM dbo.positions WHERE id=4 AND system_id=2") == 1,
            "Malformed hierarchy created a partial agreement or moved a foreign position");
        Execute(connection, "UPDATE dbo.positions SET parent_id=NULL WHERE id=4");

        var dop = SqlServerProcurement.CreateDopFromExtras(connection, 1, Revision(connection));
        Check(dop.Saved && dop.Id > 2, "Supplementary agreement not created");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.contracts WHERE id={dop.Id} AND kind=N'dop' AND base_contract_id=1") == 1,
            "Supplement parent contract broken");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id WHERE sy.contract_id={dop.Id} AND p.out_of_kp=0") == 2,
            "Extra source rows not moved into supplement");
        Check(Count(connection,
            $"SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id={extra.Id} AND kind=N'in'") == 1,
            "Historical movement lost after supplement");
        Check(Count(connection,
            $"SELECT COUNT_BIG(*) FROM dbo.procurement WHERE position_id={extra.Id} AND cost_price=81") == 1,
            "Extra procurement cost lost after supplement");

        var doomed = SqlServerProcurement.AddExtra(connection, 1, null,
            "Удаляемый материал", "шт", 5m, null, Revision(connection));
        Check(doomed.Saved, "Extra for deletion not created");
        var del = SqlServerProcurement.DeleteExtra(connection, 1, doomed.Id!.Value, Revision(connection));
        Check(del.Saved, "Extra deletion failed");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.positions WHERE id={doomed.Id}") == 0,
            "Extra deletion left a position");

        var chosen = new[] { 1L, 2L };
        var order = SqlServerProcurement.SetProc(connection, 1, chosen, "Цех", "shop_queued",
            "Ц-77", "", null, null, null, null, null, Revision(connection),
            paidDate: new DateTime(2026, 11, 21), explicitSource: "shop");
        Check(order.Saved, "Bulk order/payment failed");
        Check(Count(connection,
            "SELECT COUNT_BIG(*) FROM dbo.procurement WHERE source=N'shop' AND stage=N'shop_queued' AND paid_date='2026-11-21'") == 2,
            "Paid date or source not written");

        var lockedStart = Revision(connection);
        Execute(connection, "UPDATE dbo.contracts SET locked=1 WHERE id=1");
        AssertCannotSave(SqlServerProcurement.DeleteMovement(connection, 1, 1, originalId, lockedStart), "Locked movement deleted");
        Check(Count(connection, $"SELECT COUNT_BIG(*) FROM dbo.movements WHERE id={originalId}") == 1, "Movement lost");
        AssertCannotSave(SqlServerProcurement.SetProc(connection, 1, chosen, "Other", "paid",
            "wrong", null, null, null, null, null, null, lockedStart),
            "Locked contract accepted order");
        AssertCannotSave(SqlServerProcurement.AddMovements(connection, 1,
            new[] { new SqlServerProcurement.MovementInput(1, 1m) }, "in",
            DateTime.Today, "", "", lockedStart),
            "Locked contract accepted movement");
        Check(!SqlServerProcurement.AddExtra(connection, 1, null, "Locked", "шт", 1m,
            null, lockedStart).Saved, "Locked contract accepted extra");
        Execute(connection, "UPDATE dbo.contracts SET locked=0 WHERE id=1");

        // Two editors starting with one revision: at most one can commit.
        var parallelRev = Revision(connection);
        var outcomes = new ConcurrentBag<bool>();
        Parallel.For(0, 2, i =>
        {
            try
            {
                var result = SqlServerProcurement.AddMovements(connection, 1,
                    new[] { new SqlServerProcurement.MovementInput(3, 1m) }, "in",
                    DateTime.Today, $"parallel-{i}", "", parallelRev);
                outcomes.Add(result.Saved);
            }
            catch (DBConcurrencyException) { outcomes.Add(false); }
        });
        Check(outcomes.Count == 2 && outcomes.Count(x => x) == 1,
            "Concurrent editors both committed or both failed");
        Check(Count(connection,
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE position_id=3") == 1,
            "Concurrent movements duplicated");

        // 2500+ real positions, 2500 moves; no large IN(...) command or SQL parameter limit.
        Execute(connection, @"
;WITH nums AS (
  SELECT TOP(2500) ROW_NUMBER() OVER(ORDER BY a.object_id,b.object_id) AS n
  FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.positions (system_id,name,unit,qty,[order],out_of_kp)
SELECT 1,N'Bulk '+CONVERT(NVARCHAR(20),n),N'шт',1,1000+n,0 FROM nums;");
        var bulk = new List<SqlServerProcurement.MovementInput>();
        using (var cn = new SqlConnection(connection))
        {
            cn.Open();
            using var cmd = new SqlCommand("SELECT id FROM dbo.positions WHERE name LIKE N'Bulk %' ORDER BY id", cn);
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) bulk.Add(new SqlServerProcurement.MovementInput(rd.GetInt64(0), 1m));
        }
        Check(bulk.Count == 2500, "2500 SQL positions not inserted");
        var sw = Stopwatch.StartNew();
        var large = SqlServerProcurement.AddMovements(connection, 1, bulk, "in",
            DateTime.Today, "bulk-2500", "", Revision(connection));
        sw.Stop();
        Check(large.Saved, "2500-row movement batch failed");
        Check(Count(connection,
            "SELECT COUNT_BIG(*) FROM dbo.movements WHERE doc=N'bulk-2500'") == 2500,
            "2500 movements mismatch");
        Console.WriteLine($"PROCUREMENT LARGE BATCH {bulk.Count}: {sw.ElapsedMilliseconds} ms");
        Console.WriteLine($"PROCUREMENT REAL SQL CHECKS PASSED {checks}/{checks}");
    }

    private static int Main()
    {
        var cs = Environment.GetEnvironmentVariable("GMC_PROCUREMENT_CI_SQL");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Console.Error.WriteLine("Isolated SQL Server CI connection is required.");
            return 1;
        }
        var master = new SqlConnectionStringBuilder(cs)
        {
            InitialCatalog = "master",
            ConnectTimeout = 5,
            TrustServerCertificate = true
        };
        var ready = false;
        for (var i = 0; i < 60; i++)
        {
            try
            {
                using var conn = new SqlConnection(master.ConnectionString);
                conn.Open();
                ready = true;
                break;
            }
            catch (SqlException) { Thread.Sleep(2000); }
        }
        if (!ready)
        {
            Console.Error.WriteLine("CI SQL Server did not start.");
            return 1;
        }
        var db = PasswordSafeDatabasePrefix + Guid.NewGuid().ToString("N")[..10];
        var connection = new SqlConnectionStringBuilder(master.ConnectionString)
        {
            InitialCatalog = db
        }.ConnectionString;
        var created = false;
        try
        {
            Execute(master.ConnectionString, $"CREATE DATABASE [{db}]");
            created = true;
            Run(connection);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("PROCUREMENT REAL SQL FAILURE after " + checks + " checks: " + ex);
            return 1;
        }
        finally
        {
            if (created)
            {
                SqlConnection.ClearAllPools();
                Execute(master.ConnectionString, $"ALTER DATABASE [{db}] " +
                    "SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [" + db + "]");
            }
        }
    }
}
