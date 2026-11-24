using System.Globalization;

namespace GmcSmeta.Core.Domain;

public static class ProcurementDisplay
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string Quantity(decimal value) => value.ToString("0.######", Russian);

    // No culture-dependent thousands interpretation: "1,5" must never become 15.
    public static bool TryParseNumber(string text, out decimal value) =>
        decimal.TryParse(text.Trim().Replace(" ", "").Replace("\u00A0", "").Replace("\u202F", "").Replace(',', '.'),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    public static string ExpectedCaption(DateTime? due, DateTime today) =>
        due is not DateTime date ? "" : date.ToString(date.Year == today.Year ? "dd.MM" : "dd.MM.yy", Russian);

    public static string ExpectedState(DateTime? due, string status, DateTime today)
    {
        if (due is not DateTime date) return "";
        if (status is not ("none" or "ordered" or "invoice" or "paid" or "shop_queued" or "shop_work" or "part_stock")) return "done";
        var days = (date.Date - today.Date).Days;
        return days < 0 ? "late" : days <= 3 ? "soon" : "ok";
    }

    public static string Remaining(decimal planned, decimal issued, string status, CultureInfo culture)
    {
        var left = Math.Max(0m, planned - issued);
        return status is "skip" or "zero" || left <= 0.000001m ? "" : left.ToString("0.######", culture);
    }

    public static string OrderCaption(string order, string invoice, DateTime? paid)
    {
        var text = string.Join(" / ", new[] { order, string.IsNullOrWhiteSpace(invoice) ? "" : "сч. " + invoice }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        return paid is DateTime date ? (text.Length == 0 ? "" : text + " · ") + $"опл. {date:dd.MM}" : text;
    }

    public static string NoteCaption(string note, string replacement) =>
        string.IsNullOrWhiteSpace(replacement) ? note : "замена: " + replacement +
            (string.IsNullOrWhiteSpace(note) ? "" : "; " + note);

    public static bool TryParseDue(string text, DateTime today, out DateTime? date)
    {
        date = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (text.StartsWith('+') && int.TryParse(text[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            && days <= 36500)
        {
            date = today.Date.AddDays(days);
            return true;
        }
        if (DateTime.TryParseExact(text, new[] { "d.M.yyyy", "dd.MM.yyyy", "d.M.yy" },
            CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var full))
        {
            date = full.Date;
            return true;
        }
        if (DateTime.TryParseExact(text + "." + today.Year, "d.M.yyyy",
            CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var shortDate))
        {
            date = shortDate.Date;
            return true;
        }
        return false;
    }
}
