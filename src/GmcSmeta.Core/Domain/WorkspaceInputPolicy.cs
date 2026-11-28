using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Validation of document input before any connection or write.</summary>
public static class WorkspaceInputPolicy
{
    public static ObjectProperties ValidateObject(ObjectProperties values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var clean = values.Normalize() with
        {
            Note = values.Note ?? "",
            Form = (values.Form ?? "").Trim()
        };
        if (clean.Name.Length > 255 || clean.Customer.Length > 255 || clean.Address.Length > 255 ||
            clean.Folder.Length > 500 || clean.Form.Length > 32)
            throw new ArgumentException("Слишком длинное поле объекта (название/заказчик/адрес — 255, папка — 500, форма — 32).");
        return clean;
    }

    public static SqlServerWorkspace.NewContractData ValidateContract(SqlServerWorkspace.NewContractData input)
    {
        if (input is null) throw new ArgumentNullException(nameof(input));
        var v = input with
        {
            Kind = (input.Kind ?? "").Trim(), Number = (input.Number ?? "").Trim(),
            Date = (input.Date ?? "").Trim(), Name = (input.Name ?? "").Trim()
        };
        if (v.Kind is not ("kp" or "main" or "dop"))
            throw new ArgumentException("Недопустимый тип документа.");
        if (v.Number.Length > 64 || v.Date.Length > 32 || v.Name.Length > 255)
            throw new ArgumentException("Слишком длинные реквизиты документа.");
        if (!Documents.IsValidDocumentDate(v.Date))
            throw new ArgumentException("Дата должна быть в формате дд.мм.гггг.");
        if (v.Kind == "dop" && v.BaseContractId is not > 0)
            throw new ArgumentException("Для допсоглашения выбери исходный договор.");
        if (v.AgreementId is <= 0 || v.Kind != "main" && v.AgreementId.HasValue)
            throw new ArgumentException("Юридический договор выбирается только для приложения.");
        var annexNumber = (v.AnnexNumber ?? "").Trim();
        if (annexNumber.Length > 200 || v.Kind != "main" && annexNumber.Length != 0)
            throw new ArgumentException("Номер приложения допустим только для приложения (до 200 символов).");
        return v with { BaseContractId = v.Kind == "dop" ? v.BaseContractId : null, AnnexNumber = annexNumber };
    }

}
