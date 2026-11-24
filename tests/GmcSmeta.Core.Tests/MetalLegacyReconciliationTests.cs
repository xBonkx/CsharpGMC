using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class MetalLegacyReconciliationTests
{
    private const string Category = "Бухты";
    private const string Material = "0.5x1250";
    private const string Place = "Склад";
    private static readonly DateTime Start =
        new(2025, 3, 1, 10, 15, 32, DateTimeKind.Unspecified);

    private static MetalLegacyEntry E(long id, string kind, long area = 0, int pieces = 0,
        long reserve = 0, string from = "", string to = "", long? obj = null,
        long? lot = null, string supplier = "", string invoice = "", string note = "",
        string category = Category, string material = Material) =>
        new(id, category, material, kind, area, pieces, reserve, from, to, obj,
            obj is null ? "" : "Тестовый объект", lot, supplier, invoice,
            note, "synthetic-user", Start.AddMinutes(id));

    private static MetalLegacyEntry[] Complete() =>
    [
        E(1, "Начальный запас", 10_000, 10, to: Place),
        E(2, "Потребность", 5_000, obj: 51),
        E(3, "Резерв", reserve: 4_000, from: Place, obj: 51),
        E(4, "Выдача", 2_500, 2, reserve: -2_500, from: Place, obj: 51),
        E(5, "Возврат", 1_000, 1, to: Place, obj: 51),
        E(6, "Партия", 8_000, 8, supplier: "Синтетический поставщик", invoice: "SYN-1"),
        E(7, "Оплата партии", 8_000, 8, to: MetalWarehouseRules.SupplierPlace,
            lot: 6, supplier: "Синтетический поставщик", invoice: "SYN-1"),
        E(8, "Получение партии", 3_000, 3, from: MetalWarehouseRules.SupplierPlace,
            to: Place, lot: 6, supplier: "Синтетический поставщик", invoice: "SYN-1"),
        E(9, "Получение партии", 5_000, 5, from: MetalWarehouseRules.SupplierPlace,
            to: Place, lot: 6, supplier: "Синтетический поставщик", invoice: "SYN-1")
    ];

    [Fact]
    public void FullLedger_ReconcilesStockReserveLotAndObject()
    {
        var report = MetalLegacyReconciliation.Preview(Complete());
        Assert.True(report.CanImport, string.Join("; ", report.Errors));
        Assert.Equal(9, report.EventCount);
        Assert.Equal(16_500, report.Physical);
        Assert.Equal(0, report.Supplier);
        Assert.Equal(1_500, report.Reserved);
        Assert.Equal(17, Assert.Single(report.Places.Where(p => p.Place == Place)).Pieces);
        Assert.Equal(5_000, Assert.Single(report.Objects).Demand);
        Assert.Equal(1_500, Assert.Single(report.Objects).Issued);
        var lot = Assert.Single(report.Lots);
        Assert.True(lot.Paid);
        Assert.Equal(8_000, lot.Received);
        Assert.Equal(8, lot.ReceivedPieces);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void PreviewHash_IsStableAcrossOrdering()
    {
        var history = Complete();
        Assert.Equal(MetalLegacyReconciliation.Hash(history),
            MetalLegacyReconciliation.Hash(history.Reverse().ToArray()));
        Assert.Equal(MetalLegacyReconciliation.Preview(history).SourceSha256,
            MetalLegacyReconciliation.Preview(history.Reverse().ToArray()).SourceSha256);
    }

    [Fact]
    public void AnyChangeInSourceChangesManifest()
    {
        var history = Complete();
        var altered = history.ToArray();
        altered[4] = altered[4] with { Area = 999 };
        Assert.NotEqual(MetalLegacyReconciliation.Hash(history),
            MetalLegacyReconciliation.Hash(altered));
    }

    [Fact]
    public void RepeatedIds_AreBlocking()
    {
        var history = Complete().Append(E(1, "Приход", 1000, to: Place)).ToArray();
        Assert.False(MetalLegacyReconciliation.Preview(history).CanImport);
        Assert.Contains(MetalLegacyReconciliation.Preview(history).Errors,
            x => x.Contains("Повторный"));
    }

    [Fact]
    public void MissingLot_Aborts()
    {
        var history = Complete().Where(e => e.Id != 6).ToArray();
        Assert.False(MetalLegacyReconciliation.Preview(history).CanImport);
    }

    [Fact]
    public void PaymentMustMatchParentLot()
    {
        var history = Complete();
        history[6] = history[6] with { Area = 7_999 };
        Assert.False(MetalLegacyReconciliation.Preview(history).CanImport);
    }

    [Fact]
    public void ReceiptCannotExceedLot()
    {
        var history = Complete();
        history[8] = history[8] with { Area = 6_000, Pieces = 6 };
        Assert.False(MetalLegacyReconciliation.Preview(history).CanImport);
    }

    [Fact]
    public void NegativeReservationIsRejected()
    {
        var history = new[]
        {
            E(1, "Начальный запас", 10_000, 10, to: Place),
            E(2, "Снять резерв", reserve: -1_000, from: Place, obj: 51)
        };
        Assert.False(MetalLegacyReconciliation.Preview(history).CanImport);
    }

    [Fact]
    public void IncompletePaidLot_IsWarnedNotFabricated()
    {
        var history = Complete().Where(e => e.Id != 9).ToArray();
        var preview = MetalLegacyReconciliation.Preview(history);
        Assert.True(preview.CanImport, string.Join("; ", preview.Errors));
        Assert.Equal(5_000, preview.Supplier);
        Assert.Contains(preview.Warnings, x => x.Contains("частично"));
    }

    [Fact]
    public void UnpaidLotHasNoSupplierBalance()
    {
        var history = new[]
        {
            E(1, "Начальный запас", 5_000, 5, to: Place),
            E(2, "Партия", 8_000, 8, supplier: "SYN", invoice: "A")
        };
        var preview = MetalLegacyReconciliation.Preview(history);
        Assert.True(preview.CanImport);
        Assert.Equal(0, preview.Supplier);
        Assert.Contains(preview.Warnings, x => x.Contains("не оплачен"));
    }

    [Fact]
    public void NegativeDemandDeltaFromPythonIsPreserved()
    {
        var rows = new[]
        {
            E(1, "Начальный запас", 5_000, 5, to: Place),
            E(2, "Потребность", 4_000, obj: 51),
            E(3, "Потребность", -1_000, obj: 51)
        };
        var preview = MetalLegacyReconciliation.Preview(rows);
        Assert.True(preview.CanImport);
        Assert.Equal(3_000, Assert.Single(preview.Objects).Demand);
    }

    [Fact]
    public void HistoricalCorrectionMustHaveJournaledReason()
    {
        var rows = new[]
        {
            E(1, "Начальный запас", 5_000, 5, to: Place),
            E(2, "Корректировка", 2_000, to: Place)
        };
        Assert.False(MetalLegacyReconciliation.Preview(rows).CanImport);
        rows[1] = rows[1] with { Note = "Синтетическая причина" };
        Assert.True(MetalLegacyReconciliation.Preview(rows).CanImport);
    }

    [Fact]
    public void PiecesInNonCoilSectionAreBlocking()
    {
        var report = MetalLegacyReconciliation.Preview(
            [E(1, "Начальный запас", 1_000, 2, to: Place, category: "Листы")]);
        Assert.False(report.CanImport);
        Assert.Contains(report.Errors, x => x.Contains("единиц"));
    }

    [Fact]
    public void IssueWithoutSourceOrObjectFailsClosed()
    {
        var rows = new[]
        {
            E(1, "Начальный запас", 10_000, 10, to: Place),
            E(2, "Выдача", 2_000, 2)
        };
        var report = MetalLegacyReconciliation.Preview(rows);
        Assert.False(report.CanImport);
        Assert.Contains(report.Errors, x => x.Contains("место") || x.Contains("объекта"));
    }

    [Fact]
    public void TwoLotsWithSameInvoiceAreNotSilentlyMerged()
    {
        var rows = new[]
        {
            E(1, "Партия", 1_000, 1, supplier: "SYN", invoice: "A"),
            E(2, "Партия", 2_000, 2, supplier: "SYN", invoice: "A")
        };
        Assert.False(MetalLegacyReconciliation.Preview(rows).CanImport);
    }

    [Fact]
    public void CannotCreateReservedCoilsAsFreeStock()
    {
        var rows = new[]
        {
            E(1, "Начальный запас", 5_000, 5, to: Place),
            E(2, "Резерв", pieces: 2, reserve: 1_000, from: Place, obj: 51)
        };
        Assert.False(MetalLegacyReconciliation.Preview(rows).CanImport);
    }

    [Fact]
    public void ImporterConstructorRejectsNonPilotDatabaseWithoutOpeningSql()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new SqlServerMetalLegacyImport(
                "Server=localhost;Initial Catalog=GmcSmetaProduction;Integrated Security=True;TrustServerCertificate=True"));
    }

    [Fact]
    public void EmptySourceAlwaysAborts()
    {
        var result = MetalLegacyReconciliation.Preview([]);
        Assert.False(result.CanImport);
        Assert.Equal(0, result.EventCount);
    }
}
