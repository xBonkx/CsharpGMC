namespace GmcSmeta.Core.Domain;

public static class KsDraftDates
{
    /// <summary>Строка «дд.мм.гггг» с сообщениями Python (FullDateLineEdit.parsed_date).</summary>
    public static DateTime ParseRuDate(string text)
    {
        var parts = text.Trim().Split(new[] { '.', ',', '/', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[2].Length != 4)
            throw new FormatException("Введите дату целиком, например 10.10.2026");
        if (!int.TryParse(parts[2], out var y) || !int.TryParse(parts[1], out var m) ||
            !int.TryParse(parts[0], out var d))
            throw new FormatException("Проверьте дату: такого дня нет");
        try
        {
            return new DateTime(y, m, d);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new FormatException("Проверьте дату: такого дня нет");
        }
    }

    public static (DateTime Date, DateTime From, DateTime To, string Message) Normalize(
        string dateText, string fromText, string toText)
    {
        var date = ParseRuDate(dateText);
        var from = ParseRuDate(fromText);
        var to2 = ParseRuDate(toText);
        var message = "";
        if (to2 > date)
        {
            to2 = date;
            message = "Конец периода не может быть позже даты акта — поставлен по дату акта";
        }
        if (from > to2)
        {
            from = to2;
            message = "Начало периода было позже конца — исправлено";
        }
        return (date, from, to2, message);
    }
}
