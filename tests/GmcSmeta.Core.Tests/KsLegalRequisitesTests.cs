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

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void PreviousForeignOrUnlinkedGroupBlocksOnlyGroupedPrint(
        bool grouped, bool missingLink, bool allowed)
    {
        var p = Fixture();
        var current = p.Report.Acts[0] with { Number = "2" };
        var previous = current with { Id = 9, ContractId = 1, Number = "1", Date = current.Date.AddDays(-1) };
        var scope = p.Scope with { Acts = new[] { previous, current } };
        var legal = PrintAgreements(p);
        if (missingLink) legal.Remove(1);
        var report = grouped ? KsGroupReport.Calculate(scope, "2") : KsGroupReport.CalculateSingle(scope, current.Id);
        if (allowed)
            Assert.Equal(102L, KsLegalRequisites.RequirePrintAgreement(scope, report, legal, grouped));
        else
            Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.RequirePrintAgreement(scope, report, legal, grouped));
    }

    [Fact]
    public void GroupedPrintAllowsPriorAnnexOfSameLegalAgreement()
    {
        var p = Fixture();
        var current = p.Report.Acts[0] with { Number = "2" };
        var previous = current with { Id = 9, ContractId = 1, Number = "1", Date = current.Date.AddDays(-1) };
        var scope = p.Scope with { Acts = new[] { previous, current } };
        var legal = PrintAgreements(p);
        legal[1] = legal[1] with { SourceId = legal[2].SourceId };
        var report = KsGroupReport.Calculate(scope, "2", selectedDocumentOnly: true);
        Assert.Equal(102L, KsLegalRequisites.RequirePrintAgreement(scope, report, legal, true));
        Assert.Single(report.Previous);
    }

    [Fact]
    public void GroupedPrintAllowsFutureForeignActButRejectsItsOwnGroup()
    {
        var p = Fixture();
        var current = p.Report.Acts[0];
        var future = current with { Id = 9, ContractId = 1, Number = "2", Date = current.Date.AddDays(1) };
        var scope = p.Scope with { Acts = new[] { current, future } };
        var legal = PrintAgreements(p);
        Assert.Equal(102L, KsLegalRequisites.RequirePrintAgreement(scope,
            KsGroupReport.Calculate(scope, "1"), legal, true));
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.RequirePrintAgreement(scope,
            KsGroupReport.Calculate(scope, "2"), legal, true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportedOpeningRejectsAmbiguousLegalOwnership(bool grouped)
    {
        var p = Fixture();
        var current = p.Report.Acts[0];
        var scope = p.Scope with { Opening = new Ks3OpeningBalance(current.Date.AddDays(-1), 100m) };
        var legal = PrintAgreements(p);
        var report = grouped ? KsGroupReport.Calculate(scope, "1") : KsGroupReport.CalculateSingle(scope, current.Id);
        Assert.True(report.GrossCumulative);
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.RequirePrintAgreement(scope, report, legal, grouped));
        legal[1] = legal[1] with { SourceId = legal[2].SourceId };
        Assert.Equal(102L, KsLegalRequisites.RequirePrintAgreement(scope, report, legal, grouped));
        Assert.Equal(100m, report.SinceStartNet - report.PeriodGross);
    }

    [Fact]
    public void OpeningBeforeItsEffectiveDateDoesNotBlockNormalSingleReport()
    {
        var p = Fixture();
        var current = p.Report.Acts[0];
        var scope = p.Scope with { Opening = new Ks3OpeningBalance(current.Date, 100m) };
        var report = KsGroupReport.CalculateSingle(scope, current.Id);
        Assert.False(report.GrossCumulative);
        Assert.Equal(102L, KsLegalRequisites.RequirePrintAgreement(scope, report, PrintAgreements(p), false));
    }

    [Fact]
    public void MissingSelectedLinkBlocksPrintEvenWhenCurrentActHasLegalRequisites()
    {
        var p = Fixture();
        var legal = PrintAgreements(p);
        legal.Remove(2);
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.RequirePrintAgreement(p.Scope, p.Report, legal, true));
    }

    [Theory]
    [InlineData(101L, true)]
    [InlineData(102L, false)]
    [InlineData(0L, false)]
    public void OverviewRequiresAllDocumentsEvenWithoutActs(long otherLegalId, bool allowed)
    {
        var p = Fixture();
        var mapping = new Dictionary<long, long> { [2] = 101 };
        if (otherLegalId != 0) mapping[1] = otherLegalId;
        if (allowed)
            KsLegalScopePolicy.RequireOverview(p.Scope, mapping);
        else
            Assert.Throws<InvalidOperationException>(() => KsLegalScopePolicy.RequireOverview(p.Scope, mapping));
    }

    [Fact]
    public void OverviewCannotBypassMissingSelectedMappingOnEmptyScope()
    {
        var p = Fixture();
        var scope = p.Scope with { Acts = Array.Empty<SqlServerActs.ActInfo>() };
        Assert.Throws<InvalidOperationException>(() =>
            KsLegalScopePolicy.RequireOverview(scope, new Dictionary<long, long>()));
    }

    [Fact]
    public void OverviewRejectsActWithUnexpectedDocumentEvenWhenAllDocumentsMatch()
    {
        var p = Fixture();
        var scope = p.Scope with { Acts = new[] { p.Report.Acts[0] with { ContractId = 999 } } };
        Assert.Throws<InvalidOperationException>(() =>
            KsLegalScopePolicy.RequireOverview(scope, new Dictionary<long, long> { [1] = 101, [2] = 101 }));
    }

    [Fact]
    public void FinancialPreviewUsesSameGuardAsPrintForAmbiguousOpening()
    {
        var p = Fixture();
        var act = p.Report.Acts[0];
        var scope = p.Scope with { Opening = new Ks3OpeningBalance(act.Date.AddDays(-1), 100m) };
        var report = KsGroupReport.CalculateSingle(scope, act.Id);
        var print = PrintAgreements(p);
        var ownership = print.ToDictionary(pair => pair.Key, pair => pair.Value.SourceId);
        Assert.Throws<InvalidOperationException>(() => KsLegalScopePolicy.RequireReport(scope, report, ownership, false));
        Assert.Throws<InvalidOperationException>(() => KsLegalRequisites.RequirePrintAgreement(scope, report, print, false));
        ownership[1] = ownership[2];
        print[1] = print[1] with { SourceId = print[2].SourceId };
        Assert.Equal(KsLegalRequisites.RequirePrintAgreement(scope, report, print, false),
            KsLegalScopePolicy.RequireReport(scope, report, ownership, false));
    }

    [Fact]
    public void MissingSameAgreementAnnexBlocksOverviewAndGroupedButNotSingleWithoutOpening()
    {
        var p = Fixture();
        var mapping = new Dictionary<long, long> { [1] = 101, [2] = 101, [3] = 101 };
        Assert.Throws<InvalidOperationException>(() => KsLegalScopePolicy.RequireOverview(p.Scope, mapping));
        Assert.Throws<InvalidOperationException>(() => KsLegalScopePolicy.RequireReport(p.Scope, p.Report, mapping, true));
        Assert.Equal(101L, KsLegalScopePolicy.RequireReport(p.Scope,
            KsGroupReport.CalculateSingle(p.Scope, p.Report.Acts[0].Id), mapping, false));
        mapping[3] = 202;
        KsLegalScopePolicy.RequireOverview(p.Scope, mapping);
        Assert.Equal(101L, KsLegalScopePolicy.RequireReport(p.Scope, p.Report, mapping, true));
    }

    [Fact]
    public void OmittedAnnexBlocksSingleWithHistoricalOpening()
    {
        var p = Fixture();
        var act = p.Report.Acts[0];
        var scope = p.Scope with { Opening = new Ks3OpeningBalance(act.Date.AddDays(-1), 100m) };
        var mapping = new Dictionary<long, long> { [1] = 101, [2] = 101, [3] = 101 };
        var report = KsGroupReport.CalculateSingle(scope, act.Id);
        Assert.True(report.GrossCumulative);
        Assert.Throws<InvalidOperationException>(() => KsLegalScopePolicy.RequireReport(scope, report, mapping, false));
    }

    private static Dictionary<long, KsLegalRequisites.Agreement> PrintAgreements(SqlServerKsPrintData.Package p) =>
        new()
        {
            [1] = new(101, "Д-42", "23.01.2025", p.Contractors[1]),
            [2] = new(102, "Д-42", "23.01.2025", p.Contractors[1])
        };

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
