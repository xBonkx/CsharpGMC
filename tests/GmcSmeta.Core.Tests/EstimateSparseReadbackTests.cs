using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

// PERF-014: точечное авторитетное перечитывание. Sparse-результат обязан
// совпадать с полным reload той же ревизии; любой не-ячеечный сценарий — null.
public sealed class EstimateSparseReadbackTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly int _contract;
    private readonly long _system;
    private readonly long _sub;
    private readonly long _top;
    private readonly long _child;
    private readonly long _work;
    private readonly long _outside;
    private readonly long _otherSystemRow;

    public EstimateSparseReadbackTests()
    {
        _conn.Open();
        Database.Exec(_conn, "PRAGMA foreign_keys=ON");
        Database.EnsureSchema(_conn);
        // Форма с round_rows: ветка округления сумм разделов и правил форм.
        Database.Exec(_conn,
            "INSERT INTO tags ([key], name, note, rules) VALUES ('tst', 'Тест-форма', '', '{\"round_rows\": true}')");
        var obj = Documents.CreateObject(_conn, "Объект", form: "tst");
        _contract = checked((int)Documents.CreateContract(_conn, obj, "kp", "1", "01.01.2026", "Смета"));
        _system = Documents.AddSystem(_conn, _contract, "Вентиляция");
        _sub = Documents.AddSystem(_conn, _contract, "Подраздел", _system);
        _top = Documents.AddPosition(_conn, _system, "Вентилятор", "шт", 2m, 100m);
        _child = Documents.AddPosition(_conn, _system, "Фильтр", "шт", 5m, 0m, parentPositionId: _top);
        _work = Documents.AddPosition(_conn, _system, "Монтаж системы", "шт", 1m, 500m);
        _outside = Documents.AddPosition(_conn, _system, "Дополнительно", "шт", 3m, 70m);
        Database.Exec(_conn, $"UPDATE positions SET out_of_kp = 1 WHERE id = {_outside}");
        _otherSystemRow = Documents.AddPosition(_conn, _sub, "Клапан", "шт", 4m, 30m);
        RowWriter.Update(_conn, "positions", _child, new Dictionary<string, object?>
        { ["purchase_price"] = 12.5d, ["work_price"] = 5d });
        var other = Documents.AddSystem(_conn, _contract, "Отопление");
        Documents.AddPosition(_conn, other, "Радиатор", "шт", 1m, 200m);
    }

    [Fact]
    public void SparseMatchesFullReloadAfterSingleCellEdits()
    {
        var before = EstimateReader.Load(_conn, _contract);

        // Эмуляция подтверждённых SaveCell-правок: qty, name (работа → не-работа),
        // note, unit и purchase_price — разные строки, как в реальном burst.
        RowWriter.Update(_conn, "positions", _top, new Dictionary<string, object?> { ["qty"] = 3d });
        RowWriter.Update(_conn, "positions", _work, new Dictionary<string, object?>
        { ["name"] = "Демонтаж старой системы", ["unit"] = "компл." });
        RowWriter.Update(_conn, "positions", _otherSystemRow, new Dictionary<string, object?>
        { ["note"] = "примечание после сверки", ["purchase_price"] = 21.5d });

        var full = EstimateReader.Load(_conn, _contract);
        var sparse = EstimateReader.ReadSparse(_conn, _contract, before,
            new List<long> { _top, _work, _otherSystemRow });

        Assert.NotNull(sparse);
        AssertDocumentsEqual(full, sparse!);
        // Правки реально видны: количество и статистика работ изменились.
        Assert.NotEqual(before.Totals.Total, sparse!.Totals.Total);
        Assert.Contains("Демонтаж", sparse.Rows.Single(r => r.Id == _work).Name);
        var topRow = sparse.Rows.Single(r => r.Id == _top);
        Assert.Equal(3m, topRow.Qty);
        Assert.Equal(Money.Round2(3m * 100m), topRow.Amount);
    }

    [Fact]
    public void SparseMatchesFullReloadWithoutEditsToo()
    {
        var full = EstimateReader.Load(_conn, _contract);
        var sparse = EstimateReader.ReadSparse(_conn, _contract, full, new List<long> { _top });
        Assert.NotNull(sparse);
        AssertDocumentsEqual(full, sparse!);
    }

    [Fact]
    public void SparseFallsBackWhenStructureMoved()
    {
        var before = EstimateReader.Load(_conn, _contract);
        // Позиция переехала в другую систему — это не ячеечная правка.
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = "UPDATE positions SET system_id = " +
                "(SELECT id FROM systems WHERE name = 'Отопление') WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", _otherSystemRow);
            cmd.ExecuteNonQuery();
        }
        var sparse = EstimateReader.ReadSparse(_conn, _contract, before, new List<long> { _otherSystemRow });
        Assert.Null(sparse);
    }

    [Fact]
    public void SparseFallsBackOnMissingOrForeignPositions()
    {
        var current = EstimateReader.Load(_conn, _contract);
        Assert.Null(EstimateReader.ReadSparse(_conn, _contract, current, new List<long> { 987654321 }));
        Assert.Null(EstimateReader.ReadSparse(_conn, _contract, current, Array.Empty<long>()));
        // Чужой документ/чужой scope — сразу полный reload.
        Assert.Null(EstimateReader.ReadSparse(_conn, _contract + 1, current, new List<long> { _top }));
    }

    [Fact]
    public void SparseReadbackWhitelistIsExactlyTheSaveCellFieldSet()
    {
        var safe = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in new[] { "qty", "price", "purchase_price", "work_price", "name", "unit", "note", "area_manual", "basis", "out_of_kp", "system_id", "formulas" })
            if (EstimateReader.IsSparseReadbackSafe(field))
                safe.Add(field);
        Assert.Equal(new[] { "name", "note", "price", "purchase_price", "qty", "unit", "work_price" },
            safe.OrderBy(f => f, StringComparer.Ordinal));
        // Специальные колонки и структура — всегда полный reload.
        Assert.False(EstimateReader.IsSparseReadbackSafe("area_manual"));
        Assert.False(EstimateReader.IsSparseReadbackSafe("basis"));
        Assert.False(EstimateReader.IsSparseReadbackSafe("out_of_kp"));
        Assert.False(EstimateReader.IsSparseReadbackSafe(""));
    }

    private static void AssertDocumentsEqual(EstimateDocument full, EstimateDocument sparse)
    {
        Assert.Equal(full.Rows, sparse.Rows);
        Assert.Equal(full.PositionsInOrder, sparse.PositionsInOrder);
        Assert.Equal(full.Totals, sparse.Totals);
        Assert.Equal(full.Statistics, sparse.Statistics);
        Assert.Equal(full.ContractId, sparse.ContractId);
        Assert.Equal(full.Title, sparse.Title);
        Assert.Equal(full.KindTitle, sparse.KindTitle);
        Assert.Equal(full.ObjectName, sparse.ObjectName);
        Assert.Equal(full.Company, sparse.Company);
        Assert.Equal(full.Form, sparse.Form);
        Assert.Equal(full.VatRate, sparse.VatRate);
        Assert.Equal(full.VatTop, sparse.VatTop);
        Assert.Equal(full.Locked, sparse.Locked);
        Assert.Equal(full.Systems, sparse.Systems);
        Assert.Equal(full.Positions, sparse.Positions);
        Assert.Equal(full.SmrMode, sparse.SmrMode);
        Assert.Equal(full.SmrPercent, sparse.SmrPercent);
        Assert.Equal(full.MoneyFromStages, sparse.MoneyFromStages);
        Assert.Equal(full.BasisAllowed, sparse.BasisAllowed);
        Assert.Equal(full.PrintManualNumbering, sparse.PrintManualNumbering);
        Assert.Equal(full.KpPhase, sparse.KpPhase);
        Assert.Equal(full.SupportsFinalDiscount, sparse.SupportsFinalDiscount);
        Assert.Equal(full.DeclaredMaterial, sparse.DeclaredMaterial);
        Assert.Equal(full.DeclaredTotal, sparse.DeclaredTotal);
        Assert.Equal(full.SourceCheck, sparse.SourceCheck);
    }

    public void Dispose() => _conn.Dispose();
}
