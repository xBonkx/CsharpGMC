namespace GmcSmeta.Core.Domain;

/// <summary>Legal identity of the documents contributing to one construction-object row.
/// This metadata deliberately does not repartition or recalculate any money.</summary>
public sealed record DirectorLegalScope(int AgreementCount, int LinkedDocumentCount, int UnlinkedDocumentCount)
{
    public static DirectorLegalScope FromLinks(IEnumerable<long?> activeSameObjectLinks)
    {
        var ids = new HashSet<long>();
        var linked = 0;
        var unlinked = 0;
        foreach (var link in activeSameObjectLinks)
        {
            if (link is long id && id > 0) { ids.Add(id); linked++; }
            else unlinked++;
        }
        return new(ids.Count, linked, unlinked);
    }

    public bool HasWarning => AgreementCount > 1 || UnlinkedDocumentCount > 0;
    public string LabelSuffix => UnlinkedDocumentCount > 0
        ? " · есть документы без связи"
        : AgreementCount > 1 ? $" · договоров: {AgreementCount}" : "";

    public string Explanation =>
        $"Итоги по объекту, не по отдельному юридическому договору. Действующих договоров в расчёте: {AgreementCount}; " +
        $"связанных приложений и ДС: {LinkedDocumentCount}." +
        (UnlinkedDocumentCount > 0
            ? $" Без действующей связи: {UnlinkedDocumentCount}. Их суммы также включены; проверьте реквизиты."
            : "") +
        " Двойной щелчок открывает карточку объекта.";
}
