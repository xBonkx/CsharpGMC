using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Отображение журнала изменений — перенос history_dialog.py без SQL и WPF:
/// русские подписи entity/action, цвет строки, срезание названия документа из
/// details и поиск по нескольким словам в склеенной строке таблицы.
/// </summary>
public static class HistoryPresentation
{
    /// <summary>ENTITY из history_dialog.py; неизвестное значение показываем как есть.</summary>
    public static string EntityLabel(string entity) => entity switch
    {
        "object" => "Объект",
        "contract" => "Документ",
        "system" => "Система",
        "position" => "Строка",
        "procurement" => "Закупка",
        "movement" => "Склад",
        "act" => "Акт КС",
        "shipment" => "Ведомость",
        "smr_cost" => "Затраты СМР",
        "catalog" => "Справочник",
        // C#-события, которых нет в словаре Python; оставлять их сырыми в интерфейсе незачем.
        "metal_register" => "Склад металла",
        "person" => "Человек",
        "company" => "Организация",
        "tag" => "Тег",
        _ => entity ?? "",
    };

    /// <summary>ACTION из history_dialog.py; неизвестное значение показываем как есть.</summary>
    public static string ActionLabel(string action) => action switch
    {
        "create" => "создано",
        "update" or "edit" => "изменено",
        "delete" => "удалено",
        "move" => "перенесено",
        "import" => "загружено из Excel",
        "undo" => "отмена (Ctrl+Z)",
        "redo" => "возврат (Ctrl+Y)",
        "copy" => "скопировано",
        "rename" => "переименовано",
        "line" => "строка акта",
        "in" => "пришло на склад",
        "out" => "выдано на объект",
        "back" => "возврат с объекта",
        "back_supplier" => "возврат поставщику",
        "fix" => "выдано на объект",
        "unfix" => "выдача отменена",
        "attach" => "файл прикреплён",
        "detach" => "файл убран",
        "discount" => "скидка",
        "archive" => "в архив",
        "archive_year" => "год архива",
        "merge" => "объединено",
        "restore" => "возвращено из корзины",
        "sign" => "подписан",
        "unsign" => "подпись снята",
        // C#-события, которых нет в словаре Python (встречаются в пилотном change_log).
        "request" => "заявка",
        "spec_update" => "обновление спецификации",
        "access_list" => "список допуска",
        "orders" => "приказы",
        "unarchive" => "из архива",
        _ => action ?? "",
    };

    /// <summary>
    /// Группа цвета строки как TINT в history_dialog.py: delete — красный,
    /// undo/redo — жёлтый, create/import — зелёный, иначе без заливки.
    /// </summary>
    public static string? TintKey(string action) => action switch
    {
        "delete" => "delete",
        "undo" or "redo" => "undo",
        "create" or "import" => "create",
        _ => null,
    };

    /// <summary>
    /// short() из history_dialog.py: если details начинается с названия документа,
    /// его убираем — в заголовке окна оно уже есть. Префиксы проверяются по порядку.
    /// </summary>
    public static string ShortDetails(string? details, IEnumerable<string>? prefixes)
    {
        var text = details ?? "";
        if (prefixes is null) return text;
        foreach (var prefix in prefixes)
        {
            if (!string.IsNullOrEmpty(prefix) &&
                text.StartsWith(prefix, StringComparison.Ordinal))
                return text[prefix.Length..];
        }
        return text;
    }

    /// <summary>
    /// Поиск history_dialog.apply: все слова запроса должны встречаться в одной
    /// строке без учёта регистра. Пустой запрос не скрывает ни одной строки.
    /// </summary>
    public static string[] SearchWords(string? query) =>
        string.IsNullOrWhiteSpace(query) ? Array.Empty<string>() :
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    public static bool MatchesWords(string? query, string? line) =>
        MatchesSearchWords(SearchWords(query), line);

    public static bool MatchesSearchWords(IReadOnlyList<string> words, string? line)
    {
        var text = line ?? "";
        foreach (var word in words)
            if (!text.Contains(word, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Дата как в Python: dd.MM.yyyy HH:mm, пустая дата — пустая строка.</summary>
    public static string FormatTimestamp(DateTime? timestamp) =>
        timestamp is { } value
            ? value.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)
            : "";
}
