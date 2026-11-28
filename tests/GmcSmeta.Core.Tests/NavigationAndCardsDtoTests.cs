using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Tests;

// PERF-007.01/.02: независимая сверка DTO. Навигация и карточки сверяются
// с независимыми сырыми чтениями тех же таблиц и полным чтением сметы.
public sealed class NavigationAndCardsDtoTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly long _active;
    private readonly long _archived;
    private readonly long _kp;
    private readonly long _main;

    public NavigationAndCardsDtoTests()
    {
        _conn.Open();
        Database.Exec(_conn, "PRAGMA foreign_keys=ON");
        Database.EnsureSchema(_conn);
        _active = Documents.CreateObject(_conn, "Активный объект", customer: "ООО «ГМЦ СТРОЙ»",
            address: "ул. Ленина, 1", folder: "Папка 2026");
        _archived = Documents.CreateObject(_conn, "Архивный объект");
        RowWriter.Update(_conn, "objects", _archived,
            new Dictionary<string, object?> { ["archived"] = true, ["archived_on"] = "15.03.2026" });
        _kp = Documents.CreateContract(_conn, _active, "kp", "12", "01.02.2026", "Коммерческое");
        _main = Documents.CreateContract(_conn, _active, "main", "3", "05.03.2026", "Приложение А");
        RowWriter.Update(_conn, "contracts", _main,
            new Dictionary<string, object?> { ["kp_phase"] = "prices", ["base_contract_id"] = _kp });
        FillEstimate(_kp);
        FillEstimate(_main);
    }

    private void FillEstimate(long contract)
    {
        var system = Documents.AddSystem(_conn, contract, "Вентиляция");
        Documents.AddPosition(_conn, system, "Вентилятор", "шт", 2m, 100m);
        Documents.AddPosition(_conn, system, "Монтаж системы", "шт", 1m, 500m);
    }

    [Fact]
    public void NavigationObjectsMatchRawTableAndArchiveScope()
    {
        var objects = Workspace.Objects(_conn);
        // Архивный scope: и активные, и архивные; порядок — archived, name.
        Assert.Equal(2, objects.Count);
        Assert.False(objects[0].Archived);
        Assert.True(objects[1].Archived);
        Assert.Contains("(архив)", objects[1].DisplayName);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, customer, address, archived, archived_on, created_at, folder " +
            "FROM objects ORDER BY archived, name";
        using var r = cmd.ExecuteReader();
        foreach (var row in objects)
        {
            Assert.True(r.Read());
            Assert.Equal((long)row.Id, (long)r.GetInt64(0));
            Assert.Equal(row.Name, r.GetString(1));
            Assert.Equal(row.Customer, r.GetString(2));
            Assert.Equal(row.Address, r.GetString(3));
            Assert.Equal(row.Archived, r.GetInt64(4) != 0);
            Assert.Equal(row.ArchivedOn, r.IsDBNull(5) ? "" : r.GetString(5));
            Assert.Equal(row.CreatedAt, r.IsDBNull(6) ? "" : r.GetString(6));
            Assert.Equal(row.Folder, r.IsDBNull(7) ? "" : r.GetString(7));
        }
        Assert.False(r.Read());
    }

    [Fact]
    public void NavigationDocumentsMatchRawTableAndTotals()
    {
        var documents = Workspace.Documents(_conn);
        var totals = Totals.ReadAll(_conn).ToDictionary(t => t.Id);
        Assert.Equal(2, documents.Count);

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, object_id, kind, number, [date], name, locked, stage_of, stage_no, " +
            "stage_rest, kp_phase, base_contract_id, \"order\", company_id FROM contracts ORDER BY object_id, \"order\", id";
        using var r = cmd.ExecuteReader();
        foreach (var doc in documents)
        {
            Assert.True(r.Read());
            Assert.Equal((long)doc.Id, r.GetInt64(0));
            Assert.Equal((long)doc.ObjectId, r.GetInt64(1));
            Assert.Equal(doc.Kind, r.GetString(2));
            Assert.Equal(doc.Number, r.GetString(3));
            Assert.Equal(doc.Date, r.IsDBNull(4) ? "" : r.GetString(4));
            Assert.Equal(doc.Name, r.IsDBNull(5) ? "" : r.GetString(5));
            Assert.Equal(doc.Locked, r.GetInt64(6) != 0);
            Assert.Equal(doc.StageOf is null ? null : (long?)r.GetInt64(7), r.IsDBNull(7) ? null : r.GetInt64(7));
            Assert.Equal((long)doc.StageNo, r.IsDBNull(8) ? 0L : r.GetInt64(8));
            Assert.Equal(doc.StageRest, !r.IsDBNull(9) && r.GetInt64(9) != 0);
            Assert.Equal(doc.KpPhase, r.IsDBNull(10) ? "" : r.GetString(10));
            Assert.Equal(doc.BaseContractId is null ? null : (long?)r.GetInt64(11), r.IsDBNull(11) ? null : r.GetInt64(11));
            Assert.Equal((long)doc.Order, r.IsDBNull(12) ? 0L : r.GetInt64(12));
            Assert.Equal(doc.CompanyId, r.IsDBNull(13) ? null : r.GetInt64(13));
            // Денежные поля DocRow — ровно авторитетные итоги, ничего третьего.
            var t = totals[doc.Id];
            Assert.Equal(t.Materials, doc.Materials);
            Assert.Equal(t.Smr, doc.Smr);
            Assert.Equal(t.Total, doc.Total);
            Assert.Equal(t.Positions, doc.Positions);
        }
        Assert.False(r.Read());
    }

    [Fact]
    public void ObjectCardsTotalsMatchIndependentFullEstimates()
    {
        var obj = Workspace.Objects(_conn).Single(o => !o.Archived);
        var documents = Workspace.Documents(_conn);

        foreach (var mode in new[] { NavigationMode.Contracts, NavigationMode.Kp, NavigationMode.Archive })
        {
            var page = Workspace.ReadObjectDocuments(_conn, obj, documents, mode);
            var docs = documents.Where(d => d.ObjectId == obj.Id && (mode == NavigationMode.Archive ||
                (mode == NavigationMode.Kp ? d.Kind == "kp" : d.Kind is "main" or "dop"))).ToList();
            Assert.Equal(docs.Count, page.Cards.Count);
            foreach (var card in page.Cards)
            {
                // Карточка обязана совпадать с полным чтением сметы по всем сводным полям.
                var full = EstimateReader.Load(_conn, card.Id);
                Assert.Equal(full.Totals, card.Estimate.Totals);
                Assert.Equal(full.Statistics, card.Estimate.Statistics);
                Assert.Equal(full.Positions, card.Estimate.Positions);
                Assert.Equal(full.Systems, card.Estimate.Systems);
                Assert.Equal(full.VatTop, card.Estimate.VatTop);
                Assert.Equal(full.Company, card.Estimate.Company);
            }
            // Итоги страницы — сумма только по CountedDocumentIds, как в Python.
            var counted = Workspace.CountedDocumentIds(documents.Where(d => d.ObjectId == obj.Id).ToList(), mode);
            var countedCards = page.Cards.Where(c => counted.Contains(c.Id)).ToList();
            Assert.Equal(countedCards.Sum(c => c.Total), page.Total);
            Assert.Equal(countedCards.Sum(c => c.Estimate.Statistics.Materials), page.Materials);
            Assert.Equal(countedCards.Sum(c => c.Estimate.Statistics.Smr), page.Smr);
            Assert.Equal(countedCards.Where(c => c.Estimate.VatTop).Sum(c => c.Estimate.Totals.Vat), page.VatTop);
        }
    }

    [Fact]
    public void ObjectCardsReadCancelsBeforeAnyWork()
    {
        var obj = Workspace.Objects(_conn).Single(o => !o.Archived);
        var documents = Workspace.Documents(_conn);
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Workspace.ReadObjectDocuments(_conn, obj, documents, NavigationMode.Contracts, cts.Token));
    }

    public void Dispose() => _conn.Dispose();
}
