using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

/// <summary>
/// Копирование документа сверяется с Python-версией построчно:
///   • реквизиты и режимы (СМР, транспорт, ПНР, тег, организация, НДС, договор для ДС);
///   • разделы: порядок, названия, подчинённость, суммы СМР, заголовки, номера заказчика;
///   • позиции в порядке показа: номер, наименование, ед., количество, цена, закуп,
///     работы, формулы, примечания и связи комплектующих.
/// Эталон снимает tools/copy_dump.py настоящим builder.copy_contract на временной копии базы.
/// </summary>
public class ContractCopyParityTests
{
    [Fact]
    public void CopyContract_MatchesPythonRowByRow()
    {
        var basePath = FindFile("db/base.db");
        var jsonPath = FindFile("db/copy_python.json");
        if (basePath is null || jsonPath is null) return;

        var temp = Path.Combine(Path.GetTempPath(), "gmcsmeta-copy-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(basePath, temp, true);
        try
        {
            using var conn = Database.OpenBase(temp);
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var problems = new List<string>();
            var comparedRows = 0;

            foreach (var source in doc.RootElement.EnumerateObject())
            {
                var sourceId = int.Parse(source.Name, CultureInfo.InvariantCulture);
                var expected = source.Value;
                var newId = Documents.CopyContract(conn, sourceId,
                    Text(expected, "kind"), Text(expected, "number"),
                    Text(expected, "date"), Text(expected, "name"));
                var info = Documents.ReadContract(conn, newId);

                Must("вид", info.Kind, Text(expected, "kind"), problems);
                Must("номер", info.Number, Text(expected, "number"), problems);
                Must("дата", info.Date, Text(expected, "date"), problems);
                Must("название", info.Name, Text(expected, "name"), problems);
                Must("организация", info.CompanyId, Long(expected, "company_id"), problems);
                Must("договор для ДС", info.BaseContractId, Long(expected, "base_contract_id"), problems);
                Num("НДС", Number(expected, "vat_rate"), info.VatRate, problems);
                Must("тег", info.Form, Text(expected, "form"), problems);
                Must("режим СМР", info.SmrMode, Text(expected, "smr_mode"), problems);
                Num("процент СМР", Number(expected, "smr_percent"), info.SmrPercent, problems);
                Num("сумма СМР", Number(expected, "smr_amount"), info.SmrAmount, problems);
                Num("транспорт", Number(expected, "transport_amount"), info.TransportAmount, problems);
                Must("режим м²", info.M2Mode, Text(expected, "m2_mode"), problems);
                Num("пусконаладка", Number(expected, "pnr_amount"), info.PnrAmount, problems);
                Num("стоимость транспорта", NumberN(expected, "transport_cost") ?? 0, info.TransportCost ?? 0, problems);
                Must("примечание транспорта", info.TransportNote, Text(expected, "transport_note"), problems);
                Must("участок КС", info.KsSitePart, Text(expected, "ks_site_part"), problems);

                // Разделы: тот же порядок, названия, подчинённость и суммы.
                var systems = LoadSystems(conn, newId);
                var expectedSystems = expected.GetProperty("systems").EnumerateArray().ToList();
                if (systems.Count != expectedSystems.Count)
                    problems.Add($"документ {sourceId}: разделов {systems.Count} вместо {expectedSystems.Count}");
                var systemIndex = new Dictionary<long, int>();
                for (var i = 0; i < systems.Count; i++) systemIndex[systems[i].Id] = i;
                for (var i = 0; i < Math.Min(systems.Count, expectedSystems.Count); i++)
                {
                    var sy = systems[i];
                    var e = expectedSystems[i];
                    Must($"раздел {i} название", sy.Name, Text(e, "name"), problems);
                    Must($"раздел {i} порядок", sy.Order, Long(e, "order"), problems);
                    Num($"раздел {i} сумма СМР", Number(e, "smr_amount"), sy.SmrAmount, problems);
                    Must($"раздел {i} заголовок", sy.Heading, Text(e, "heading"), problems);
                    Must($"раздел {i} номер заказчика", sy.SourceNum, Text(e, "source_num"), problems);
                    var parent = sy.ParentId is long p && systemIndex.ContainsKey(p)
                        ? (long)systemIndex[p] : -1L;
                    Must($"раздел {i} родитель", parent, Long(e, "parent"), problems);
                }

                // Позиции в том же порядке показа, с теми же значениями.
                var expectedRows = expected.GetProperty("rows").EnumerateArray().ToList();
                var myRows = LoadRowsInDisplayOrder(conn, systems, out var rowSystemIndex);
                if (myRows.Count != expectedRows.Count)
                    problems.Add($"документ {sourceId}: строк {myRows.Count} вместо {expectedRows.Count}");

                for (var i = 0; i < Math.Min(myRows.Count, expectedRows.Count); i++)
                {
                    var row = myRows[i];
                    var e = expectedRows[i];
                    comparedRows++;
                    Must($"строка {i} раздел", (long)rowSystemIndex[i], Long(e, "system"), problems);
                    Must($"строка {i} родитель", (long)row.ParentIndex, Long(e, "parent"), problems);
                    Must($"строка {i} номер", row.Num, Text(e, "num"), problems);
                    Must($"строка {i} наименование", row.Name, Text(e, "name"), problems);
                    Must($"строка {i} ед.", row.Unit, Text(e, "unit"), problems);
                    Num($"строка {i} количество", Number(e, "qty"), row.Qty, problems);
                    Num($"строка {i} цена", Number(e, "price"), row.Price, problems);
                    Num($"строка {i} закуп", NumberN(e, "purchase_price") ?? 0, row.PurchasePrice ?? 0, problems);
                    Must($"строка {i} порядок", row.Order, Long(e, "order"), problems);
                    Must($"строка {i} примечание", row.Note, Text(e, "note"), problems);
                    Must($"строка {i} обоснование", row.Basis, Text(e, "basis"), problems);
                    Num($"строка {i} цена работ", NumberN(e, "work_price") ?? 0, row.WorkPrice ?? 0, problems);
                    Num($"строка {i} продажа работ", NumberN(e, "work_sell") ?? 0, row.WorkSell ?? 0, problems);
                    Num($"строка {i} стоимость работ", NumberN(e, "work_cost") ?? 0, row.WorkCost ?? 0, problems);
                    Num($"строка {i} площадь", NumberN(e, "area_manual") ?? 0, row.AreaManual ?? 0, problems);
                    Must($"строка {i} примечание затрат", row.CostNote, Text(e, "cost_note"), problems);
                    Must($"строка {i} цвет затрат", row.CostColor, Text(e, "cost_color"), problems);
                    Must($"строка {i} формулы", row.Formulas, Text(e, "formulas"), problems);
                    Must($"строка {i} доп. данные", row.ExtraJson, Text(e, "extra_json"), problems);
                    Must($"строка {i} строка файла", row.SourceRow, Long(e, "source_row"), problems);
                    Must($"строка {i} каталог", row.CatalogItemId, Long(e, "catalog_item_id"), problems);
                    Assert.False(row.OutOfKp, $"документ {sourceId}, строка {i}: «вне КП» попала в копию");
                }

                // Копия обязана считать те же итоги, что исходный документ.
                var before = Totals.ReadOne(conn, sourceId)!;
                var after = Totals.ReadOne(conn, (int)newId)!;
                Num($"итоги материалов из {sourceId}", before.Materials, after.Materials, problems);
                Num($"итоги СМР из {sourceId}", before.Smr, after.Smr, problems);
                Num($"итоги без НДС из {sourceId}", before.Net, after.Net, problems);
                Num($"итоги с НДС из {sourceId}", before.Total, after.Total, problems);
                Num($"НДС из {sourceId}", before.Vat, after.Vat, problems);
            }

            Assert.True(comparedRows > 2500, $"сверено строк: {comparedRows}");
            Assert.True(problems.Count == 0, string.Join("\n", problems.Take(40)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(temp);
        }
    }

    private static string? FindFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private sealed record SystemRow(long Id, string Name, long? ParentId, long Order,
        string Heading, string SourceNum, double SmrAmount);

    private static List<SystemRow> LoadSystems(SqliteConnection conn, long contractId)
    {
        var list = new List<SystemRow>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, name, parent_id, \"order\", heading, source_num, smr_amount " +
            "FROM systems WHERE contract_id = $id ORDER BY \"order\", id";
        cmd.Parameters.AddWithValue("$id", contractId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SystemRow(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? 0 : r.GetInt64(3),
                r.IsDBNull(4) ? "" : r.GetString(4), r.IsDBNull(5) ? "" : r.GetString(5),
                r.IsDBNull(6) ? 0d : r.GetDouble(6)));
        return list;
    }

    private sealed record RowData(long Id, long? ParentId, string Num, string Name, string Unit,
        double Qty, double Price, double? PurchasePrice, long Order, string Note, string Basis,
        double? WorkPrice, double? WorkSell, double? WorkCost, double? AreaManual,
        string CostNote, string CostColor, string Formulas, string ExtraJson,
        long? SourceRow, long? CatalogItemId, bool OutOfKp, int ParentIndex);

    private static List<RowData> LoadRowsInDisplayOrder(SqliteConnection conn,
        List<SystemRow> systems, out List<int> systemIndex)
    {
        var all = new List<RowData>();
        systemIndex = new List<int>();
        for (var si = 0; si < systems.Count; si++)
        {
            var rows = new List<RowData>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, parent_id, num, name, unit, qty, price, purchase_price, \"order\", " +
                    "note, basis, work_price, work_sell, work_cost, area_manual, cost_note, " +
                    "cost_color, formulas, extra_json, source_row, catalog_item_id, out_of_kp " +
                    "FROM positions WHERE system_id = $id ORDER BY \"order\", id";
                cmd.Parameters.AddWithValue("$id", systems[si].Id);
                using var r = cmd.ExecuteReader();
                string Text(int i) => r.IsDBNull(i) ? "" : r.GetString(i);
                long? Id(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
                double Dbl(int i) => r.IsDBNull(i) ? 0d : r.GetDouble(i);
                double? DblN(int i) => r.IsDBNull(i) ? null : r.GetDouble(i);
                while (r.Read())
                    rows.Add(new RowData(r.GetInt64(0), Id(1), Text(2), Text(3), Text(4),
                        Dbl(5), Dbl(6), DblN(7), r.IsDBNull(8) ? 0 : r.GetInt64(8), Text(9),
                        Text(10), DblN(11), DblN(12), DblN(13), DblN(14), Text(15), Text(16),
                        Text(17), Text(18), Id(19), Id(20), Dbl(21) != 0, -1));
            }

            var children = rows.Where(p => p.ParentId is not null)
                .GroupBy(p => p.ParentId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Order).ToList());
            var display = new List<RowData>();
            foreach (var top in rows.Where(p => p.ParentId is null).OrderBy(p => p.Order))
            {
                display.Add(top);
                if (children.TryGetValue(top.Id, out var kids)) display.AddRange(kids);
            }

            var local = display.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
            for (var i = 0; i < display.Count; i++)
            {
                display[i] = display[i] with
                {
                    ParentIndex = display[i].ParentId is long parent && local.TryGetValue(parent, out var pi)
                        ? pi : -1
                };
                all.Add(display[i]);
                systemIndex.Add(si);
            }
        }
        return all;
    }

    private static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : null;

    private static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0d;

    private static double? NumberN(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : null;

    private static void Must(string label, object? actual, object? expected, List<string> problems)
    {
        if (Equals(actual, expected)) return;
        problems.Add($"{label}: C# «{actual}» вместо Python «{expected}»");
    }

    private static void Num(string label, decimal expected, decimal actual, List<string> problems)
    {
        if (expected == actual) return;
        problems.Add($"{label}: C# {actual} вместо Python {expected}");
    }

    private static void Num(string label, double expected, double actual, List<string> problems)
    {
        if (Math.Abs(expected - actual) <= 1e-9) return;
        problems.Add($"{label}: C# {actual:R} вместо Python {expected:R}");
    }
}
