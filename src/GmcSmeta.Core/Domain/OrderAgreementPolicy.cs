namespace GmcSmeta.Core.Domain;

/// <summary>Legal defaults for new orders only; no rewriting historic documents.</summary>
public static class OrderAgreementPolicy
{
    public sealed record Agreement(long Id, string Number, string Date, long? CompanyId,
        string CustomerFull, string CustomerOrganization);

    public static AccessSite Resolve(AccessSite site, IReadOnlyList<Agreement> choices)
    {
        var match = choices.Count == 1 && choices[0].Id > 0 ? choices[0] : null;
        return site with
        {
            LegalAgreementRequired = true,
            AgreementId = match?.Id,
            AgreementCompanyId = match?.CompanyId,
            ContractNumber = match?.Number.Trim() ?? "",
            ContractDate = match?.Date.Trim() ?? "",
            Customer = match is null ? "" : string.IsNullOrWhiteSpace(match.CustomerOrganization)
                ? match.CustomerFull.Trim() : match.CustomerOrganization.Trim()
        };
    }

    public static string? BlockReason(AccessSite site, IReadOnlyList<AccessCompany> companies)
    {
        if (!site.LegalAgreementRequired) return null;
        if (site.AgreementId is null)
            return "Не выбран однозначный действующий договор. Откройте приказы из нужного приложения или ДС.";
        if (site.AgreementCompanyId is not long id ||
            companies.FirstOrDefault(c => c.Id == id) is not { } company ||
            string.IsNullOrWhiteSpace(company.SignerName))
            return "В юридическом договоре не указан подрядчик либо в его организации не заполнен подписант.";
        return null;
    }

    public static AccessCompany? Company(AccessSite site, IReadOnlyList<AccessCompany> companies)
    {
        if (BlockReason(site, companies) is { } reason)
            throw new InvalidOperationException(reason);
        if (site.LegalAgreementRequired)
            return companies.Single(c => c.Id == site.AgreementCompanyId);
        // Preserve the established fallback only for legacy schemas.
        return companies.FirstOrDefault(c => c.Name.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase))
            ?? companies.FirstOrDefault();
    }
}
