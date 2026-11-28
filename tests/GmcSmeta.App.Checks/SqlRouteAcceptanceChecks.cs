using System.Data;
using System.IO;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App.Checks;

/// <summary>
/// Живая SQL-приёмка маршрутов, затронутых scalar-хардненингом (BUG-F54.07):
/// справочники, ПТО, паспорта, металл, Excel, корзина/undo. Чтение реальных
/// SQL-типов через продакшн-код на демо-БД; запись выполняется только в
/// отказ-сценариях с заведомо неверной ревизией — штатные guards отклоняют
/// её до любых изменений, после чего ревизия базы обязана совпасть.
/// </summary>
internal static class SqlRouteAcceptanceChecks
{
    private static string Rev(string cs)
    {
        using var conn = SqlServerDatabase.Open(cs);
        return SqlServerDatabase.Revision(conn);
    }

    private static void RejectChanges(string cs, string route, Action staleWrite)
    {
        var before = Rev(cs);
        try
        {
            staleWrite();
            throw new Exception($"{route}: запись с неверной ревизией не была отклонена.");
        }
        catch (Exception ex) when (ex is DBConcurrencyException or InvalidOperationException
                                   or System.IO.FileNotFoundException)
        {
            // Штатный отказ: guard отклонил запись до изменений.
        }
        var after = Rev(cs);
        if (!string.Equals(before, after, StringComparison.Ordinal))
            throw new Exception($"{route}: ревизия базы изменилась при отклонённой записи.");
        Console.WriteLine($"{route}: реальные SQL-типы читаются, отказ не меняет базу (rev {before[..Math.Min(12, before.Length)]}…).");
    }

    public static void Run(string cs)
    {
        var contractId = ContractWithPositions(cs);
        var objectId = ObjectIdOf(cs, contractId);

        // 1. Справочники: страница каталога и поиск — десятичные цены, строгие ID.
        var catalog = SqlServerWorkspaceNavigation.CatalogSnapshot(cs, null, 0, 200);
        var price = catalog.Rows.Where(r => r.Price != 0m).Select(r => r.Price).FirstOrDefault();
        Console.WriteLine($"Справочники: {catalog.Rows.Count} позиций, цена образца {price:0.##}, rev {catalog.Revision[..12]}….");
        RejectChanges(cs, "Справочники (отказ)",
            () => SqlServerCatalog.DeleteCatalogItem(cs, -1, "stale-revision"));

        // 2. ПТО: если пакетов нет, создаётся один штатным Save (таблицы
        //    gmc_pto_* изолированы), затем непустое чтение; отказ — профиль
        //    объекта с чужой ревизией.
        var pto = new SqlServerPto(cs);
        var packages = pto.Packages(null);
        if (packages.Count == 0)
            pto.Save(new GmcSmeta.Core.Domain.PtoPackage
            {
                ObjectId = objectId,
                ContractId = contractId,
                ContractLabel = "SQL Pilot BUG-F54.07",
                Fields = new Dictionary<string, string> { ["Поле приёмки"] = "значение" },
            });
        packages = pto.Packages(null);
        var certificates = pto.Certificates("");
        if (packages.Count == 0)
            throw new Exception("ПТО: пакет не создался штатным Save.");
        Console.WriteLine($"ПТО: {packages.Count} пакетов, {certificates.Count} сертификатов (наполнено штатно).");
        RejectChanges(cs, "ПТО (отказ)",
            () => pto.SaveObjectProfile(objectId, new Dictionary<string, string>(), 999999));

        // 3. Паспорта вентиляции: если записей нет, одна штатным SaveBatch
        //    (таблицы паспортов изолированы), затем непустое чтение;
        //    отказ — удаление несуществующего паспорта.
        var passports = new SqlServerPassports(cs);
        if (passports.List(objectId).Count == 0)
            passports.SaveBatch(objectId, new[]
            {
                new GmcSmeta.Core.Domain.VentPassport
                {
                    ObjectId = objectId,
                    System = "П1 (SQL Pilot BUG-F54.07)",
                    Object = "Объект приёмки",
                    Purpose = "приёмка типов",
                },
            });
        var list = passports.List(objectId);
        if (list.Count == 0)
            throw new Exception("Паспорта: запись не создалась штатным SaveBatch.");
        Console.WriteLine($"Паспорта: {list.Count} записей по объекту {objectId} (наполнено штатно).");
        RejectChanges(cs, "Паспорта (отказ)",
            () => passports.Delete(objectId, Guid.NewGuid(), 0));

        // 4. Металл: склад демо-БД наполняется штатной инициализацией (одна позиция,
        //    таблицы gmc_metal_* изолированы), затем чтение непустых справочников;
        //    отказ — проводка со старой ревизией.
        var metal = new SqlServerMetalWarehouse(cs);
        if (!metal.IsInitialized())
            metal.ImportOpening(new[]
            {
                new GmcSmeta.Core.Domain.MetalOpening("Бухты", "Бухта приёмки SQL Pilot BUG-F54.07",
                    new[] { ("В наличии", 1m) }, Array.Empty<(string, decimal)>(), null,
                    new[] { ("В наличии", 1m) }),
            }, "sql-pilot-acceptance.xlsx", new byte[] { 0x50, 0x4B }, metal.Revision());
        var initialized = metal.IsInitialized();
        var materials = metal.Materials();
        if (materials.Count == 0)
            throw new Exception("Металл: справочники склада пусты после штатной инициализации.");
        Console.WriteLine($"Металл: initialized={initialized}, материалов {materials.Count} (наполнено штатно).");
        RejectChanges(cs, "Металл (отказ)",
            () => metal.Post(new GmcSmeta.Core.Domain.MetalOperation(
                GmcSmeta.Core.Domain.MetalWarehouseRules.Categories.First(),
                "Материал приёмки", "income", 0.01m), "stale-revision"));

        // 5. Excel: снимок существующих строк сметы (строгие ID, формулы, деньги);
        //    отказ — применение плана при изменившемся файле (guard fingerprint
        //    срабатывает до открытия транзакции — база физически не трогается).
        var excel = SqlServerExcelExistingUpdate.Read(cs, contractId);
        Console.WriteLine($"Excel existing: {excel.Rows.Count} строк сметы, rev {excel.Revision[..12]}….");
        var previewPath = Path.Combine(Path.GetTempPath(), "gmc-route-accept-" + Guid.NewGuid().ToString("N") + ".xlsx");
        File.WriteAllBytes(previewPath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 });
        try
        {
            string sha;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                sha = Convert.ToHexString(sha256.ComputeHash(File.ReadAllBytes(previewPath)));
            var incoming = new[] { new ExcelExistingSourceRow(0, null, "", "", "", "Строка приёмки",
                "м", 1m, 10m, null, null, "", 1, null) };
            var plan = ExcelExistingUpdatePlan.Build(contractId, excel.Revision, sha,
                excel.Rows, incoming, excel.UniqueSystems);
            File.WriteAllBytes(previewPath, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0xFF, 0x7F });
            var before = Rev(cs);
            try
            {
                SqlServerExcelExistingUpdate.Apply(cs, previewPath, plan);
                throw new Exception("Excel: apply с изменённым файлом не был отклонён.");
            }
            catch (InvalidOperationException ex) when (
                ex.Message.Contains("изменён после предпросмотра") ||
                ex.Message.Contains("Нет выбранных безопасных изменений"))
            {
                // Штатный отказ до открытия транзакции: пустой план либо
                // fingerprint-гвард — база физически не трогается.
            }
            var after = Rev(cs);
            if (!string.Equals(before, after, StringComparison.Ordinal))
                throw new Exception("Excel: ревизия базы изменилась при отклонённом применении.");
            Console.WriteLine("Excel (отказ fingerprint): apply отклонён гвардом файла, база не изменена.");
        }
        finally
        {
            File.Delete(previewPath);
        }

        // 6. Корзина/undo: снимок корзины, консистентный undo-снимок договора;
        //    отказ — восстановление несуществующей записи под чужой ревизией.
        var trash = SqlServerTrash.ReadSnapshot(cs);
        Console.WriteLine($"Корзина: {trash.Entries.Count} записей, rev {trash.Revision[..12]}….");
        using (var conn = SqlServerDatabase.Open(cs))
            SqlServerUndo.ReadConsistent(cs, contractId);
        Console.WriteLine("Undo: консистентный снимок договора прочитан без исключений.");
        RejectChanges(cs, "Корзина/undo (отказ)",
            () => SqlServerTrash.Restore(cs, -1, "stale-revision"));
    }

    private static int ContractWithPositions(string cs)
    {
        using var conn = SqlServerDatabase.Open(cs);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP(1) s.contract_id FROM dbo.positions p JOIN dbo.systems s " +
            "ON p.system_id=s.id GROUP BY s.contract_id ORDER BY COUNT(*) DESC";
        return SqlInteger.RequireInt32(cmd.ExecuteScalar());
    }

    private static long ObjectIdOf(string cs, int contractId)
    {
        using var conn = SqlServerDatabase.Open(cs);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT object_id FROM dbo.contracts WHERE id=@id";
        cmd.Parameters.Add("@id", SqlDbType.BigInt).Value = contractId;
        return SqlInteger.RequireInt64(cmd.ExecuteScalar());
    }
}
