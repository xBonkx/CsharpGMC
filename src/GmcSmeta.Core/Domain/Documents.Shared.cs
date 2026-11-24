namespace GmcSmeta.Core.Domain;

/// <summary>
/// Провайдер-независимые хелперы документов — используются и SQL Server-модулями.
/// Легаси-операции SQLite остаются в Documents.cs (см. PORTING_CHECKLIST_SQLITE.md).
/// </summary>
public static partial class Documents
{
    /// <summary>Заголовок документа как Contract.title в Python: «КП № 12 от 01.02.2026 — название».</summary>
    public static string ContractTitleText(string kind, string number, string date, string name)
    {
        var title = kind switch { "main" => "Договор", "dop" => "Допсоглашение", _ => "КП" };
        if (number.Length > 0) title += $" № {number}";
        if (date.Length > 0) title += $" от {date}";
        if (name.Length > 0) title += $" — {name}";
        return title;
    }

    /// <summary>Пустая дата допустима (пусто = без даты), иначе дд.мм.гггг.</summary>
    public static bool IsValidDocumentDate(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return true;
        return DateTime.TryParseExact(value, new[] { "dd.MM.yyyy", "d.M.yyyy" },
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }
}
