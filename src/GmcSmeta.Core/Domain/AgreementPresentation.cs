namespace GmcSmeta.Core.Domain;

/// <summary>Display-only projection. Does not determine legal identity, KS scope or persistence.</summary>
public static class AgreementPresentation
{
    public static string? Heading(IEnumerable<(DocRow Document, string Company)> source)
    {
        var documents = source.Where(x => x.Document.Kind is "main" or "dop").ToArray();
        var mains = documents.Where(x => x.Document.Kind == "main").ToArray();
        if (mains.Length == 0) return null;
        var first = mains[0];
        var number = first.Document.Number.Trim();
        var date = first.Document.Date.Trim();
        var company = first.Company.Trim();
        if (number.Length == 0 || date.Length == 0 || company.Length == 0) return null;
        if (mains.Any(x => x.Document.ObjectId != first.Document.ObjectId ||
            x.Document.Number.Trim() != number || x.Document.Date.Trim() != date ||
            x.Company.Trim() != company)) return null;
        var ids = mains.Select(x => x.Document.Id).ToHashSet();
        if (documents.Any(x => x.Document.ObjectId != first.Document.ObjectId ||
            x.Company.Trim() != company ||
            (x.Document.Kind == "dop" && (x.Document.BaseContractId is not int id || !ids.Contains(id)))))
            return null;
        return $"Договор № {number} от {date}";
    }

    public static string AnnexTitle(DocRow doc) =>
        string.IsNullOrWhiteSpace(doc.Name) ? $"Приложение (ID {doc.Id})" : doc.Name.Trim();

    public static string FullAnnexTitle(DocRow doc) =>
        AnnexTitle(doc) + (string.IsNullOrWhiteSpace(doc.Number) ? "" : " · договор № " + doc.Number.Trim()) +
        (string.IsNullOrWhiteSpace(doc.Date) ? "" : " от " + doc.Date.Trim());

    // Tree is loaded before company information: retain the legal number, unlike a verified-looking
    // short title. Existing NavigationName and NavigationTip are deliberately not changed.
    public static string NavigationName(DocRow doc) => doc.Kind == "main"
        ? AnnexTitle(doc) + (string.IsNullOrWhiteSpace(doc.Number) ? "" : " · № " + doc.Number.Trim())
        : doc.NavigationName;
}
