namespace GmcSmeta.Core.Db;

public sealed record ExcelImportLegalTarget(long Id, long ObjectId, string Number, string Date, long? CompanyId, bool Archived)
{
    public string Caption => $"№ {Number} от {Date} (ID {Id})";
}

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
        if (choice.AgreementId is <= 0 || choice.Kind != "main" && choice.AgreementId is not null)
            throw new ArgumentException("Явный юридический договор допустим только для приложения.");
    }

    public static void ValidateLegalTarget(ExcelImportChoice choice, IReadOnlyList<ExcelImportLegalTarget> cards)
    {
        if (choice.Kind != "main") return;
        if (cards.Count == 0 && choice.AgreementId is null) return;
        var target = cards.SingleOrDefault(a => a.Id == choice.AgreementId && !a.Archived)
            ?? throw new InvalidOperationException("Выберите действующий юридический договор для приложения.");
        if (target.CompanyId != choice.CompanyId ||
            !string.Equals(target.Number, choice.Number.Trim(), StringComparison.Ordinal) ||
            !string.Equals(target.Date, choice.Date.Trim(), StringComparison.Ordinal))
            throw new InvalidOperationException("Номер, дата или организация приложения не совпадают с выбранным договором. Подставьте его реквизиты.");
    }

    public static void ValidateNewDraftBatch(IReadOnlyList<ExcelImportChoice> annexes)
    {
        // New object with no cards retains creation of one draft legal card.
        // Do not collapse different source legal contexts into that first card.
        if (annexes.Select(a => (a.Number.Trim(), a.Date.Trim(), a.CompanyId)).Distinct().Skip(1).Any())
            throw new InvalidOperationException("Приложения относятся к разным реквизитам. Импортируйте отдельными пакетами после создания договоров.");
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
