using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Tests;

public sealed class KsLegalRequisitesTests
{
    [Fact]
    public void DsUsesBaseAgreementDateAndContractorAndKeepsOwnSupplement()
    {
        var p = Fixture();
        var legal = KsLegalRequisites.ForDocument(p, 2);
        Assert.Equal("Д-42", legal.Number);
        Assert.Equal("23.01.2025", legal.Date);
        Assert.Equal("ООО Подрядчик", legal.Contractor.LegalName);
        var ds = KsLegalRequisites.ForAct(p, p.Report.Acts[0]);
        Assert.Equal(new KsLegalRequisites.Supplement("4", "11.08.2026"), ds);
    }

    [Fact]
    public void ExplicitActSupplementOverridesDocumentAndExtraLinesArePreserved()
    {
        var p = Fixture();
        var act = p.Report.Acts[0] with { DsNo = "№ 5", DsDate = "2026-09-09", DsExtra = "Первое основание\r\nВторое основание\nПервое основание" };
        var supplements = KsLegalRequisites.ForActSupplements(p, act);
        Assert.Equal(new[] { "№5 от 09.09.2026", "Первое основание", "Второе основание" }, supplements.Select(s => s.Label));
    }

    [Fact]
    public void MissingBaseIsNeverReplacedWithFirstMain()
    {
        var p = Fixture();
        var ds = p.Scope.Documents[1] with { BaseContractId = 999 };
        p = p with { Scope = p.Scope with { Documents = new[] { p.Scope.Documents[0], ds } } };
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.ForDocument(p, 2));
    }

    [Fact]
    public void UnlinkedDsRequiresExactlyOneMain()
    {
        var p = Fixture();
        var ds = p.Scope.Documents[1] with { BaseContractId = null };
        p = p with { Scope = p.Scope with { Documents = new[] { p.Scope.Documents[0], ds } } };
        Assert.Equal("23.01.2025", KsLegalRequisites.ForDocument(p, 2).Date);
        p = p with { Scope = p.Scope with { Documents = p.Scope.Documents.Append(p.Scope.Documents[0] with { Id = 3 }).ToArray() } };
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.ForDocument(p, 2));
    }

    [Fact]
    public void SummaryRejectsConflictingMainDatesWithoutChangingScope()
    {
        var p = Fixture();
        var other = p.Scope.Documents[0] with { Id = 3 };
        var contractors = p.Contractors.ToDictionary(x => x.Key, x => x.Value);
        contractors[3] = contractors[1] with { DocumentId = 3, ContractDate = "24.01.2025" };
        p = p with { Scope = p.Scope with { Documents = p.Scope.Documents.Append(other).ToArray() }, Contractors = contractors,
            Report = p.Report with { Acts = p.Report.Acts.Append(p.Report.Acts[0] with { Id = 2, ContractId = 3 }).ToArray() } };
        var ids = p.Scope.Documents.Select(d => d.Id).ToArray();
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.ForSummary(p));
        Assert.Equal(ids, p.Scope.Documents.Select(d => d.Id));
    }

    [Fact]
    public void InvalidLegalDateIsNotPrintedAsDsDate()
    {
        var p = Fixture();
        var contractors = p.Contractors.ToDictionary(x => x.Key, x => x.Value);
        contractors[1] = contractors[1] with { ContractDate = "31.02.2025" };
        p = p with { Contractors = contractors };
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.ForDocument(p, 2));
    }

    private static SqlServerKsPrintData.Package Fixture()
    {
        var main = new SqlServerKsContractScope.Document(1, 10, "main", "Д-42", "Приложение", null, "dns", 22m, "");
        var ds = main with { Id = 2, Kind = "dop", Number = "4", BaseContractId = 1 };
        var act = new SqlServerActs.ActInfo(1, "1", new DateTime(2026, 11, 27), null, null, false, 0, 0, 2);
        var scope = new SqlServerKsContractScope.Scope(2, "Д-42", new[] { main, ds }, new[] { act });
        var report = KsGroupReport.Calculate(scope, "1");
        var requisites = new SqlServerKsRequisites.Values(10, "Стройка", "Заказчик", "", "", "", "", "", "", "", "", "", "", "");
        return new(scope, report, requisites, Array.Empty<SqlServerKsPrintData.Line>(), new Dictionary<long, SqlServerKsPrintData.Contractor>
        {
            [1] = new(1, "2025-01-23 12:00:00", "Подрядчик", "ООО Подрядчик", "Директор", "Иванов"),
            [2] = new(2, "2026-08-11", "Другая компания ДС", "Другая компания ДС", "", "")
        });
    }
}
