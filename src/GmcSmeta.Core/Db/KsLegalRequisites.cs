using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>Print-only resolution of legacy main/DS metadata. Does not alter KS grouping.</summary>
public static class KsLegalRequisites
{
    public sealed record Agreement(long SourceId, string Number, string Date,
        SqlServerKsPrintData.Contractor Contractor);
    public sealed record Supplement(string Number, string Date)
    {
        public string? FreeText { get; init; }
        public string Label => FreeText ?? "№" + Number + (Date.Length == 0 ? "" : " от " + Date);
    }

    public static Agreement ForDocument(SqlServerKsPrintData.Package package, long documentId)
    {
        if (package.LegalAgreements.TryGetValue(documentId, out var legal)) return legal;
        var doc = package.Scope.Documents.Single(d => d.Id == documentId);
        var main = doc;
        if (doc.Kind == "dop")
        {
            var candidates = package.Scope.Documents.Where(d => d.ObjectId == doc.ObjectId && d.Kind == "main");
            if (doc.BaseContractId is long baseId)
                main = candidates.SingleOrDefault(d => d.Id == baseId)
                    ?? throw new InvalidOperationException("Для печати КС не найдено корректное основание ДС.");
            else
            {
                var mains = candidates.ToArray();
                if (mains.Length != 1)
                    throw new InvalidOperationException("Для печати КС выберите основание ДС: основной договор неоднозначен.");
                main = mains[0];
            }
        }
        var contractor = package.Contractors[main.Id];
        var date = NormalizeDate(contractor.ContractDate);
        return new(main.Id, main.Number.Trim(), date, contractor with { ContractDate = date });
    }

    /// <summary>
    /// Guard schema-32 print packages without changing historical KS grouping or amounts.
    /// A grouped report includes previous groups across the legacy number-based scope;
    /// a single report normally includes only the selected document's previous acts.
    /// An imported opening balance is still keyed by object and legacy number, not legal ID.
    /// </summary>
    public static long RequirePrintAgreement(SqlServerKsContractScope.Scope scope, KsGroupReport report,
        IReadOnlyDictionary<long, Agreement> legalAgreements, bool groupReport)
    {
        if (!legalAgreements.TryGetValue(scope.SelectedDocumentId, out var selected) || selected.SourceId <= 0)
            throw new InvalidOperationException("Выбранное приложение не привязано к действующему юридическому договору.");

        bool SameAgreement(long documentId) =>
            legalAgreements.TryGetValue(documentId, out var legal) && legal.SourceId == selected.SourceId;

        if (report.Acts.Count == 0 || report.Acts.Any(a => !SameAgreement(a.ContractId)))
            throw new InvalidOperationException("КС содержит акты другого договора или без привязки. Проверьте принадлежность приложений.");

        if (groupReport)
        {
            var previousKeys = report.Previous.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
            if (scope.Acts.Any(a => previousKeys.Contains(KsWorkspaceProjection.GroupKey(a)) &&
                                    !SameAgreement(a.ContractId)))
                throw new InvalidOperationException("В накопительные итоги КС-3 входят предыдущие акты другого договора или без привязки. Проверьте принадлежность приложений.");
        }

        // Even when previous acts are absent, the old opening marker cannot be
        // assigned to a specific legal ID if several agreements share its key.
        if (report.GrossCumulative && scope.Documents.Any(d => !SameAgreement(d.Id)))
            throw new InvalidOperationException("Начальный остаток КС-3 относится к неоднозначной группе договоров. Проверьте его принадлежность перед выгрузкой.");

        return selected.SourceId;
    }

    public static Agreement ForSummary(SqlServerKsPrintData.Package package)
    {
        var agreements = package.Report.Acts.Select(a => ForDocument(package, a.ContractId)).ToArray();
        if (agreements.Length == 0) throw new InvalidOperationException("Нет актов для печати реквизитов договора.");
        var first = agreements[0];
        if (package.Report.Acts.Any(a => package.LegalAgreements.ContainsKey(a.ContractId)) &&
            package.Report.Acts.Select(a => package.LegalAgreements.TryGetValue(a.ContractId, out var source) ? source.SourceId : 0).Distinct().Count() != 1)
            throw new InvalidOperationException("Общая КС-3 затрагивает разные юридические договоры. Проверьте привязку приложений.");
        if (agreements.Any(a => a.Number != first.Number || a.Date != first.Date ||
                a.Contractor.CompanyName != first.Contractor.CompanyName || a.Contractor.LegalName != first.Contractor.LegalName ||
                a.Contractor.SignerPosition != first.Contractor.SignerPosition || a.Contractor.SignerName != first.Contractor.SignerName))
            throw new InvalidOperationException("У актов общей КС-3 различаются реквизиты основного договора. Проверьте источники реквизитов.");
        return first;
    }

    public static Supplement? ForAct(SqlServerKsPrintData.Package package, SqlServerActs.ActInfo act)
    {
        var number = act.DsNo.Trim().TrimStart('№', ' ');
        var date = act.DsDate;
        if (number.Length == 0)
        {
            var doc = package.Scope.Documents.Single(d => d.Id == act.ContractId);
            if (doc.Kind != "dop") return null;
            number = doc.Number.Trim().TrimStart('№', ' ');
            date = package.Contractors[doc.Id].ContractDate;
        }
        return number.Length == 0 ? null : new(number, NormalizeDate(date));
    }

    public static IReadOnlyList<Supplement> ForActSupplements(SqlServerKsPrintData.Package package, SqlServerActs.ActInfo act)
    {
        var rows = new List<Supplement>();
        if (ForAct(package, act) is { } primary) rows.Add(primary);
        rows.AddRange(act.DsExtra.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => new Supplement("", "") { FreeText = line }));
        return rows.Distinct().ToArray();
    }

    public static IReadOnlyList<Supplement> ForSummarySupplements(SqlServerKsPrintData.Package package) =>
        package.Report.Acts.SelectMany(a => ForActSupplements(package, a)).Distinct().ToArray();

    private static string NormalizeDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (!AgreementPresentation.TryDate(value, out var date))
            throw new InvalidOperationException("Неверная дата договора или ДС для печати КС: " + value);
        return date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
    }
}
