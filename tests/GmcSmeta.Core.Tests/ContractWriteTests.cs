using System.Text.Json;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

// Свойства и копирование документа — на собственной базе в памяти.
public sealed class ContractWriteTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly long _object;
    private readonly long _contract;
    private readonly long _companyA;
    private readonly long _companyB;

    public ContractWriteTests()
    {
        _conn.Open();
        Database.Exec(_conn, "PRAGMA foreign_keys=ON");
        Database.EnsureSchema(_conn);
        _object = Documents.CreateObject(_conn, "Объект");
        _companyA = RowWriter.Insert(_conn, "companies", new Dictionary<string, object?>
        { ["name"] = "ООО «ГМЦ СТРОЙ»", ["vat_rate"] = 22.0, ["order"] = 0 });
        _companyB = RowWriter.Insert(_conn, "companies", new Dictionary<string, object?>
        { ["name"] = "ООО «КОНСОЛЬ»", ["vat_rate"] = 5.0, ["order"] = 1 });
        _contract = Documents.CreateContract(_conn, _object, "kp", "1", "01.01.2026", "Основной",
            _companyA, vatRate: 22m);
    }

    [Fact]
    public void CopyContract_CopiesFieldsPositionsAndHierarchy()
    {
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?>
        {
            ["kind"] = "main", ["smr_mode"] = "systems", ["smr_percent"] = 12.5,
            ["smr_amount"] = 1000d, ["transport_amount"] = 300d, ["m2_mode"] = "sys",
            ["pnr_amount"] = 50d, ["transport_cost"] = 123.45d,
            ["transport_note"] = "{\"pct\":1.2}", ["ks_site_part"] = "участок 2",
            ["form"] = "dns",
        });
        var system = Documents.AddSystem(_conn, _contract, "Вентиляция");
        RowWriter.Update(_conn, "systems", system, new Dictionary<string, object?>
        { ["heading"] = "Раздел 1", ["source_num"] = "1.1", ["smr_amount"] = 700d });
        var sub = Documents.AddSystem(_conn, _contract, "Подраздел", system);
        var parent = Documents.AddPosition(_conn, system, "Установка", "шт", 2m, 100m);
        var child = Documents.AddPosition(_conn, system, "Фильтр", "шт", 4m, 0m, parentPositionId: parent);
        Documents.AddPosition(_conn, system, "Воздуховод", "м", 3m, 50m);
        var outside = Documents.AddPosition(_conn, system, "Дополнительно", "шт", 1m, 999m);
        Documents.AddPosition(_conn, system, "Деталь вне КП", "шт", 2m, 0m, parentPositionId: outside);
        RowWriter.Update(_conn, "positions", outside, new Dictionary<string, object?> { ["out_of_kp"] = true });
        Documents.AddPosition(_conn, sub, "Позиция подраздела", "шт", 1m, 25m);
        RowWriter.Update(_conn, "positions", child,
            new Dictionary<string, object?> { ["purchase_price"] = 12.34d, ["work_price"] = 5d });
        var before = Totals.ReadOne(_conn, (int)_contract)!;

        var copyId = Documents.CopyContract(_conn, _contract, "main", "2", "05.02.2026", "Копия");

        var info = Documents.ReadContract(_conn, copyId);
        Assert.Equal("main", info.Kind);
        Assert.Equal("2", info.Number);
        Assert.Equal("05.02.2026", info.Date);
        Assert.Equal("Копия", info.Name);
        Assert.Equal(_companyA, info.CompanyId);
        Assert.Equal(22d, info.VatRate);
        Assert.Equal("systems", info.SmrMode);
        Assert.Equal(12.5d, info.SmrPercent);
        Assert.Equal(1000d, info.SmrAmount);
        Assert.Equal(300d, info.TransportAmount);
        Assert.Equal("sys", info.M2Mode);
        Assert.Equal(50d, info.PnrAmount);
        Assert.Equal(123.45d, info.TransportCost);
        Assert.Equal("{\"pct\":1.2}", info.TransportNote);
        Assert.Equal("участок 2", info.KsSitePart);
        Assert.Equal("dns", info.Form);
        Assert.Equal("", Text($"SELECT kp_phase FROM contracts WHERE id={copyId}"));

        // Два раздела, подраздел идёт за своим разделом и ссылается на его новую копию.
        var systems = Rows($"SELECT id,name,parent_id,\"order\",heading,source_num,smr_amount " +
                           $"FROM systems WHERE contract_id={copyId} ORDER BY \"order\",id");
        Assert.Equal(2, systems.Count);
        Assert.Equal("Вентиляция", systems[0][1]);
        Assert.Equal("Подраздел", systems[1][1]);
        Assert.Equal(systems[0][0], systems[1][2]);
        Assert.Equal(new[] { "0", "1" }, systems.Select(r => r[3]));
        Assert.Equal("Раздел 1", systems[0][4]);
        Assert.Equal("1.1", systems[0][5]);
        Assert.Equal(700d, double.Parse(systems[0][6], System.Globalization.CultureInfo.InvariantCulture));

        // Строки: установка, её комплектующая, воздуховод, позиция подраздела.
        // «Вне КП» и её комплектующая не копируются.
        var rows = Rows($"SELECT id,name,unit,qty,price,purchase_price,parent_id FROM positions " +
                        $"WHERE system_id IN (SELECT id FROM systems WHERE contract_id={copyId}) " +
                        $"ORDER BY system_id,\"order\",id");
        Assert.Equal(new[] { "Установка", "Фильтр", "Воздуховод", "Позиция подраздела" },
            rows.Select(r => r[1]));
        Assert.Equal("2", rows[0][3]);
        Assert.Equal("4", rows[1][3]);
        Assert.Equal(rows[0][0], rows[1][6]);
        Assert.Equal("12.34", rows[1][5]);
        Assert.DoesNotContain(rows, r => r[1] == "Дополнительно" || r[1] == "Деталь вне КП");

        // Копия считается так же, как исходный документ.
        var after = Totals.ReadOne(_conn, (int)copyId)!;
        Assert.Equal(before.Materials, after.Materials);
        Assert.Equal(before.Smr, after.Smr);
        Assert.Equal(before.Net, after.Net);
        Assert.Equal(before.Total, after.Total);
        Assert.Equal(before.Vat, after.Vat);

        Assert.Equal(1, History("copy", copyId));
    }

    [Fact]
    public void CopyContract_WithCompany_UsesItsVat()
    {
        var copyId = Documents.CopyContract(_conn, _contract, "kp", "", "", "", companyId: _companyB);
        var info = Documents.ReadContract(_conn, copyId);
        Assert.Equal(_companyB, info.CompanyId);
        Assert.Equal(5d, info.VatRate);
    }

    [Fact]
    public void LastCompanyId_ReturnsCompanyOfNewestDocument()
    {
        Assert.Equal(_companyA, Documents.LastCompanyId(_conn, _object));
        Documents.CreateContract(_conn, _object, "kp", "2", "02.01.2026", "", _companyB, vatRate: 5m);
        Assert.Equal(_companyB, Documents.LastCompanyId(_conn, _object));

        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["company_id"] = null });
        Assert.Equal(_companyB, Documents.LastCompanyId(_conn, _object));
        Assert.Null(Documents.LastCompanyId(_conn, 9999));
    }

    [Fact]
    public void CopyContract_WritesCreateAndCopyHistory()
    {
        var copyId = Documents.CopyContract(_conn, _contract, "kp", "", "", "");
        Assert.Equal(1, History("create", copyId));
        Assert.Equal(1, History("copy", copyId));
        var details = Text($"SELECT details FROM change_log WHERE action='copy' AND entity_id={copyId}");
        Assert.Contains("из «КП № 1 от 01.01.2026 — Основной»", details);
    }

    [Fact]
    public void CopyContract_WithoutPositions_CopiesOnlyTheDocument()
    {
        var system = Documents.AddSystem(_conn, _contract, "Раздел");
        Documents.AddPosition(_conn, system, "Позиция", qty: 1, price: 10);
        var copyId = Documents.CopyContract(_conn, _contract, "kp", "", "", "", withPositions: false);
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM systems WHERE contract_id={copyId}")));
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM positions WHERE system_id IN " +
            $"(SELECT id FROM systems WHERE contract_id={copyId})")));
        Assert.Equal(1, Convert.ToInt32(Text($"SELECT COUNT(*) FROM positions")));
    }

    [Fact]
    public void UpdateContract_WritesOnlyChangedFieldsAndKeepsHistory()
    {
        var before = RowWriter.Count(_conn, "change_log");

        var changed = Documents.UpdateContract(_conn, _contract, new ContractProperties(
            Kind: "main", Number: " 77 ", Date: "05.02.2026", Name: " Договор ",
            CompanyId: _companyB, Note: "примечание", Form: "dns"));

        Assert.True(changed);
        var info = Documents.ReadContract(_conn, _contract);
        Assert.Equal("main", info.Kind);
        Assert.Equal("77", info.Number);
        Assert.Equal("05.02.2026", info.Date);
        Assert.Equal("Договор", info.Name);
        Assert.Equal("примечание", info.Note);
        Assert.Equal("dns", info.Form);
        Assert.Equal(5d, info.VatRate);                 // НДС сменился вместе с организацией
        Assert.Equal(before + 1, RowWriter.Count(_conn, "change_log"));   // одна строка истории
    }

    [Fact]
    public void UpdateContract_NoChanges_WritesNothing()
    {
        var info = Documents.ReadContract(_conn, _contract);
        var before = RowWriter.Count(_conn, "change_log");
        var changed = Documents.UpdateContract(_conn, _contract, new ContractProperties(
            info.Kind, info.Number, info.Date, info.Name, info.CompanyId, info.BaseContractId,
            (decimal)info.VatRate, info.Note, info.Form));
        Assert.False(changed);
        Assert.Equal(before, RowWriter.Count(_conn, "change_log"));
    }

    [Fact]
    public void UpdateContract_NonDopClearsBaseContract()
    {
        var main = Documents.CreateContract(_conn, _object, "main", "9", "01.01.2026", "");
        Documents.UpdateContract(_conn, _contract, new ContractProperties(
            Kind: "dop", BaseContractId: main));
        Assert.Equal(main, Documents.ReadContract(_conn, _contract).BaseContractId);
        Documents.UpdateContract(_conn, _contract, new ContractProperties(
            Kind: "kp", BaseContractId: main));
        Assert.Null(Documents.ReadContract(_conn, _contract).BaseContractId);
    }

    [Fact]
    public void UpdateContract_RejectsSignedAndStageDocuments()
    {
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        Assert.Throws<InvalidOperationException>(() =>
            Documents.UpdateContract(_conn, _contract, new ContractProperties(Name: "Нельзя")));

        RowWriter.Update(_conn, "contracts", _contract,
            new Dictionary<string, object?> { ["locked"] = false, ["stage_of"] = _contract });
        Assert.Throws<InvalidOperationException>(() =>
            Documents.UpdateContract(_conn, _contract, new ContractProperties(Name: "Нельзя")));

        Assert.Equal("Основной", Documents.ReadContract(_conn, _contract).Name);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("01.02.2026", true)]
    [InlineData("1.2.2026", true)]
    [InlineData("31.02.2026", false)]
    [InlineData("2026-02-01", false)]
    [InlineData("завтра", false)]
    public void DocumentDate_IsValidatedLikePython(string value, bool valid) =>
        Assert.Equal(valid, Documents.IsValidDocumentDate(value));

    [Fact]
    public void KeepContract_StoresOnlyThisDocumentLikePython()
    {
        var system = Documents.AddSystem(_conn, _contract, "Вентиляция");
        var position = Documents.AddPosition(_conn, system, "Установка", "шт", 2m, 100m);
        var child = Documents.AddPosition(_conn, system, "Фильтр", "шт", 1m, 0m, parentPositionId: position);
        RowWriter.Insert(_conn, "procurement", new Dictionary<string, object?> { ["position_id"] = position });
        RowWriter.Insert(_conn, "movements", new Dictionary<string, object?>
        { ["position_id"] = position, ["kind"] = "in", ["qty"] = 2m });
        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
        { ["contract_id"] = _contract, ["number"] = "1" });
        RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
        { ["act_id"] = act, ["position_id"] = position, ["qty"] = 1m, ["price"] = 100m });
        RowWriter.Insert(_conn, "smr_costs", new Dictionary<string, object?> { ["contract_id"] = _contract });
        RowWriter.Insert(_conn, "attachments", new Dictionary<string, object?>
        { ["contract_id"] = _contract, ["name"] = "scan.pdf", ["path"] = @"C:\scan.pdf" });
        RowWriter.Insert(_conn, "spec_revisions", new Dictionary<string, object?> { ["contract_id"] = _contract });
        var shipment = RowWriter.Insert(_conn, "shipments", new Dictionary<string, object?>
        { ["object_id"] = _object, ["number"] = "В-1" });
        RowWriter.Insert(_conn, "shipment_lines", new Dictionary<string, object?>
        { ["shipment_id"] = shipment, ["position_id"] = child, ["qty"] = 1m });

        // Другой документ того же объекта не должен попасть в копию.
        var other = Documents.CreateContract(_conn, _object, "kp", "2", "02.01.2026", "Другой");
        var otherSystem = Documents.AddSystem(_conn, other, "Другой раздел");
        Documents.AddPosition(_conn, otherSystem, "Другая позиция", "шт", 1m, 1m);

        var trashId = Trash.KeepContract(_conn, _contract);

        Assert.Equal(1, RowWriter.Count(_conn, "trash"));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, kind, rows, title, object_name, deleted_by, data FROM trash";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(trashId, r.GetInt64(0));
        Assert.Equal("contract", r.GetString(1));
        Assert.Equal(2, r.GetInt64(2));                                    // две позиции
        Assert.Equal("КП № 1 от 01.01.2026 — Основной", r.GetString(3));
        Assert.Equal("Объект", r.GetString(4));
        Assert.Equal(ChangeLog.CurrentUser, r.GetString(5));
        var data = JsonDocument.Parse(r.GetString(6)).RootElement;

        // Тот же набор таблиц, что trash._dump_contracts — без объекта и ведомостей.
        var expected = new[]
        {
            "contracts", "systems", "positions", "procurement", "movements", "acts", "act_lines",
            "shipment_lines", "smr_costs", "attachments", "spec_revisions",
        };
        Assert.Equal(expected.OrderBy(x => x), data.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Equal(1, data.GetProperty("contracts").GetArrayLength());
        Assert.Equal(1, data.GetProperty("systems").GetArrayLength());
        Assert.Equal(2, data.GetProperty("positions").GetArrayLength());
        Assert.Equal(1, data.GetProperty("procurement").GetArrayLength());
        Assert.Equal(1, data.GetProperty("movements").GetArrayLength());
        Assert.Equal(1, data.GetProperty("acts").GetArrayLength());
        Assert.Equal(1, data.GetProperty("act_lines").GetArrayLength());
        Assert.Equal(1, data.GetProperty("shipment_lines").GetArrayLength());
        Assert.Equal(1, data.GetProperty("smr_costs").GetArrayLength());
        Assert.Equal(1, data.GetProperty("attachments").GetArrayLength());
        Assert.Equal(1, data.GetProperty("spec_revisions").GetArrayLength());
        Assert.Equal(_contract, data.GetProperty("contracts")[0].GetProperty("id").GetInt64());
        Assert.Equal(1, Convert.ToInt32(Text($"SELECT COUNT(*) FROM contracts WHERE id={other}")));
    }

    [Fact]
    public void DescribeForDelete_CountsActsAndMovements()
    {
        var system = Documents.AddSystem(_conn, _contract, "Вентиляция");
        var position = Documents.AddPosition(_conn, system, "Установка", "шт", 2m, 100m);
        RowWriter.Insert(_conn, "movements", new Dictionary<string, object?>
        { ["position_id"] = position, ["kind"] = "in", ["qty"] = 2m });
        for (var i = 0; i < 2; i++)
            RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
            { ["contract_id"] = _contract, ["number"] = i.ToString() });

        var info = Documents.DescribeContractForDelete(_conn, _contract);

        Assert.Equal("КП № 1 от 01.01.2026 — Основной", info.Title);
        Assert.Equal("Объект", info.ObjectName);
        Assert.Equal(2, info.Acts);
        Assert.Equal(1, info.Movements);
        Assert.Equal(new[] { "акты КС-2/КС-3: 2", "история закупок и выдачи: 1 записей" }, info.Details);
    }

    [Fact]
    public void DeleteContract_CopiesToTrashCascadesAndKeepsOtherDocuments()
    {
        var system = Documents.AddSystem(_conn, _contract, "Вентиляция");
        var position = Documents.AddPosition(_conn, system, "Установка", "шт", 2m, 100m);
        RowWriter.Insert(_conn, "movements", new Dictionary<string, object?>
        { ["position_id"] = position, ["kind"] = "in", ["qty"] = 2m });
        var act = RowWriter.Insert(_conn, "acts", new Dictionary<string, object?>
        { ["contract_id"] = _contract, ["number"] = "1" });
        RowWriter.Insert(_conn, "act_lines", new Dictionary<string, object?>
        { ["act_id"] = act, ["position_id"] = position, ["qty"] = 1m, ["price"] = 100m });
        var other = Documents.CreateContract(_conn, _object, "kp", "2", "02.01.2026", "Другой");
        var otherSystem = Documents.AddSystem(_conn, other, "Другой раздел");
        Documents.AddPosition(_conn, otherSystem, "Другая позиция", "шт", 1m, 1m);

        Documents.DeleteContract(_conn, _contract);

        Assert.Equal(1, RowWriter.Count(_conn, "contracts"));             // остался только второй
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM contracts WHERE id={_contract}")));
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM systems WHERE contract_id={_contract}")));
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM positions WHERE system_id={system}")));
        Assert.Equal(0, RowWriter.Count(_conn, "acts"));
        Assert.Equal(0, RowWriter.Count(_conn, "act_lines"));
        Assert.Equal(1, Convert.ToInt32(Text($"SELECT COUNT(*) FROM systems WHERE contract_id={other}")));
        Assert.Equal(1, RowWriter.Count(_conn, "trash"));
        Assert.Equal("contract", Text("SELECT kind FROM trash"));
        Assert.Equal(1, Convert.ToInt32(Text("SELECT rows FROM trash")));
        Assert.Equal("Объект: КП № 1 от 01.01.2026 — Основной", Text("SELECT details FROM change_log ORDER BY id DESC LIMIT 1"));
        Assert.Equal("delete", Text("SELECT action FROM change_log ORDER BY id DESC LIMIT 1"));
    }

    [Fact]
    public void DeleteContract_HistoryFailureRollsBackDocumentAndTrash()
    {
        Documents.AddSystem(_conn, _contract, "Вентиляция");
        Database.Exec(_conn, "CREATE TRIGGER reject_history BEFORE INSERT ON change_log BEGIN SELECT RAISE(ABORT,'test'); END");
        Assert.Throws<SqliteException>(() => Documents.DeleteContract(_conn, _contract));
        Assert.Equal(1, Convert.ToInt32(Text($"SELECT COUNT(*) FROM contracts WHERE id={_contract}")));
        Assert.Equal(0, RowWriter.Count(_conn, "trash"));
    }

    [Fact]
    public void DeleteContract_AllowsSignedDocumentLikePython()
    {
        RowWriter.Update(_conn, "contracts", _contract, new Dictionary<string, object?> { ["locked"] = true });
        Documents.DeleteContract(_conn, _contract);
        Assert.Equal(0, Convert.ToInt32(Text($"SELECT COUNT(*) FROM contracts WHERE id={_contract}")));
        Assert.Equal(1, RowWriter.Count(_conn, "trash"));
    }

    public void Dispose() => _conn.Dispose();

    private string Text(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is null or DBNull ? "" : Convert.ToString(value)!;
    }

    private int History(string action, long id) =>
        Convert.ToInt32(Text($"SELECT COUNT(*) FROM change_log WHERE action='{action}' AND entity_id={id}"));

    private List<string[]> Rows(string sql)
    {
        var list = new List<string[]>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var row = new string[r.FieldCount];
            for (var i = 0; i < r.FieldCount; i++)
                row[i] = r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i),
                    System.Globalization.CultureInfo.InvariantCulture)!;
            list.Add(row);
        }
        return list;
    }
}
