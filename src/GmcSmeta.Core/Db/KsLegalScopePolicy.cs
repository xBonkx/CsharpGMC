using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

/// <summary>Shared legal-identity guard for the historical number-based KS scope.
/// Never regroup acts, alter opening markers or change monetary calculations.</summary>
public static class KsLegalScopePolicy
{
    private static long RequireSelected(SqlServerKsContractScope.Scope scope,
        IReadOnlyDictionary<long, long> legalAgreements)
    {
        if (!legalAgreements.TryGetValue(scope.SelectedDocumentId, out var selected) || selected <= 0)
            throw new InvalidOperationException("Выбранное приложение не привязано к действующему юридическому договору.");
        return selected;
    }

    // A legacy number may omit an annex of the very same legal agreement.
    // Fail rather than silently presenting an incomplete financial overview or
    // changing historical grouping/opening balances without explicit approval.
    private static void RequireCompleteAgreement(SqlServerKsContractScope.Scope scope,
        IReadOnlyDictionary<long, long> legalAgreements, long selected)
    {
        var scopeIds = scope.Documents.Select(d => d.Id).ToHashSet();
        if (legalAgreements.Any(link => link.Value == selected && !scopeIds.Contains(link.Key)))
            throw new InvalidOperationException("В группу КС по прежнему номеру не входят все приложения юридического договора. Проверьте исторические номера и привязки.");
    }

    /// <summary>The overview aggregates the entire scope, including the document count.</summary>
    public static void RequireOverview(SqlServerKsContractScope.Scope scope,
        IReadOnlyDictionary<long, long> legalAgreements)
    {
        var selected = RequireSelected(scope, legalAgreements);
        RequireCompleteAgreement(scope, legalAgreements, selected);
        var scopeIds = scope.Documents.Select(d => d.Id).ToHashSet();
        bool SameAgreement(long documentId) =>
            legalAgreements.TryGetValue(documentId, out var legal) && legal == selected;
        if (scope.Documents.Any(d => !SameAgreement(d.Id)) ||
            scope.Acts.Any(a => !scopeIds.Contains(a.ContractId) || !SameAgreement(a.ContractId)))
            throw new InvalidOperationException("Список КС по прежнему номеру содержит приложения разных договоров или без привязки. Проверьте принадлежность приложений.");
    }

    public static long RequireReport(SqlServerKsContractScope.Scope scope, KsGroupReport report,
        IReadOnlyDictionary<long, long> legalAgreements, bool groupReport)
    {
        var selected = RequireSelected(scope, legalAgreements);
        if (groupReport || report.GrossCumulative)
            RequireCompleteAgreement(scope, legalAgreements, selected);
        var scopeIds = scope.Documents.Select(d => d.Id).ToHashSet();

        bool SameAgreement(long documentId) =>
            legalAgreements.TryGetValue(documentId, out var legal) && legal == selected;

        if (report.Acts.Count == 0 || report.Acts.Any(a => !scopeIds.Contains(a.ContractId) || !SameAgreement(a.ContractId)))
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
            throw new InvalidOperationException("Начальный остаток КС-3 относится к неоднозначной группе договоров. Проверьте его принадлежность перед расчётом или выгрузкой.");

        return selected;
    }

}
