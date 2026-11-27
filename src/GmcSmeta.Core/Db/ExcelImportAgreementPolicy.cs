namespace GmcSmeta.Core.Db;

/// <summary>New imports only. Never infer legal identity or change an existing document.</summary>
public static class ExcelImportAgreementPolicy
{
    public static void Validate(ExcelImportChoice choice)
    {
        if (choice.Kind is "main" or "dop" && choice.CompanyId is not > 0)
            throw new ArgumentException("Для приложения или ДС необходимо указать организацию.");
        if (choice.Kind == "dop" && choice.BaseContractId is not > 0)
            throw new ArgumentException("Для ДС необходимо выбрать документ-основание.");
        if (choice.Kind != "dop" && choice.BaseContractId is not null)
            throw new ArgumentException("Основание допустимо только для ДС.");
    }

    public static void ValidateBaseCompany(ExcelImportChoice choice, long? parentCompany)
    {
        if (choice.Kind != "dop" || parentCompany is not > 0 || choice.CompanyId != parentCompany)
            throw new InvalidOperationException("Основание ДС недоступно или его организация изменилась. Обновите импорт.");
    }

    public static (long? CompanyId, decimal VatRate) ResolveCompany(ExcelImportChoice choice,
        long? defaultCompanyId, IReadOnlyDictionary<long, decimal> companies)
    {
        Validate(choice);
        // Keep the existing default-company behavior for commercial proposals.
        var id = choice.Kind == "kp" ? defaultCompanyId : choice.CompanyId;
        if (id is null) return (null, 22m);
        if (!companies.TryGetValue(id.Value, out var vat))
            throw new InvalidOperationException("Выбранная организация больше не существует.");
        return (id, vat);
    }
}
