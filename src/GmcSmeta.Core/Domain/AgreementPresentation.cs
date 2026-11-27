namespace GmcSmeta.Core.Domain;

/// <summary>Display-only projection. Does not determine legal identity, KS scope or persistence.</summary>
public static class AgreementPresentation
{
    public sealed record Requisites(string Number, string Date, long CompanyId)
    {
        public string Heading => $"Договор № {Number} от {Date}";
    }

    // Only propose defaults from a consistent metadata group; never infer a legal ID.
    public static Requisites? Resolve(IEnumerable<DocRow> source)
    {
        var documents = source.Where(d => d.Kind is "main" or "dop").ToArray();
        var mains = documents.Where(d => d.Kind == "main").ToArray();
        if (mains.Length == 0) return null;
        var first = mains[0];
        var number = first.Number.Trim();
        if (number.Length == 0 || first.CompanyId is not long company || company <= 0 ||
            !TryDate(first.Date, out var date)) return null;
        if (mains.Any(d => d.ObjectId != first.ObjectId || d.Number.Trim() != number ||
            d.CompanyId != company || !TryDate(d.Date, out var other) || other != date)) return null;
        var ids = mains.Select(d => d.Id).ToHashSet();
        if (documents.Any(d => d.ObjectId != first.ObjectId || d.CompanyId != company ||
            (d.Kind == "dop" && (d.BaseContractId is not int id || !ids.Contains(id)))))
            return null;
        return new Requisites(number, date.ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture), company);
    }

    private static bool TryDate(string value, out DateTime date) =>
        DateTime.TryParseExact(value.Trim(), new[] { "d.M.yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss" },
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);

    public static string? Heading(IEnumerable<(DocRow Document, string Company)> source)
    {
        var documents = source.Where(x => x.Document.Kind is "main" or "dop").ToArray();
        if (documents.Any(x => string.IsNullOrWhiteSpace(x.Company))) return null;
        return Resolve(documents.Select(x => x.Document))?.Heading;
    }

    public static string AnnexTitle(DocRow doc) =>
        string.IsNullOrWhiteSpace(doc.Name) ? $"Приложение (ID {doc.Id})" : doc.Name.Trim();

    public static string FullAnnexTitle(DocRow doc) =>
        AnnexTitle(doc) + (string.IsNullOrWhiteSpace(doc.Number) ? "" : " · договор № " + doc.Number.Trim()) +
        (string.IsNullOrWhiteSpace(doc.Date) ? "" : " от " + doc.Date.Trim());

    // Existing NavigationName and NavigationTip are deliberately not changed.
    public static string NavigationName(DocRow doc, bool shortTitle = false) => doc.Kind == "main"
        ? AnnexTitle(doc) + (shortTitle || string.IsNullOrWhiteSpace(doc.Number) ? "" : " · № " + doc.Number.Trim())
        : doc.NavigationName;
}
