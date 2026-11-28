namespace GmcSmeta.Core.Domain;

/// <summary>
/// Last visual item in the object document grid, matching Python ObjectPage.NewCard.
/// This is presentation-only; document lists and financial totals stay unchanged.
/// </summary>
public sealed record NewContractCardItem(bool CanCreate, string Caption = "Новое КП");

public static class ObjectCardLayout
{
    public static (int Columns, double CardWidth) Measure(double viewportWidth, double actualWidth)
    {
        var candidate = double.IsFinite(viewportWidth) && viewportWidth > 0
            ? viewportWidth : double.IsFinite(actualWidth) ? actualWidth - 18 : 300;
        var width = Math.Max(13, candidate);
        var columns = Math.Clamp((int)((Math.Min(width, 1056) + 12) / 352), 1, 3);
        // The card itself may shrink below the preferred 300 DIP; the gap is
        // included exactly once. Never force a card outside the visible viewport.
        return (columns, Math.Max(1, Math.Floor(width / columns) - 12));
    }

    public const int CardsPerPage = 60;

    public static (int Page, int Pages, int First, int Count) PageRange(int count, int requestedPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var pages = Math.Max(1, (int)(((long)count + CardsPerPage - 1) / CardsPerPage));
        var page = Math.Clamp(requestedPage, 0, pages - 1);
        var first = page * CardsPerPage;
        return (page, pages, first, Math.Min(CardsPerPage, count - first));
    }

    public static (double Width, double Height)? RestoreGeometry(double savedWidth, double savedHeight,
        double workWidth, double workHeight, double minWidth, double minHeight)
    {
        var width = Math.Min(savedWidth, workWidth);
        var height = Math.Min(savedHeight, workHeight);
        return width >= minWidth && height >= minHeight ? (width, height) : null;
    }

    /// <summary>
    /// Copy real document cards in their existing order and append exactly one
    /// creation item, including for an empty list. Does not mutate SQL data.
    /// </summary>
    public static IReadOnlyList<object> AppendNewContract<T>(IReadOnlyList<T> cards, bool canCreate, string caption = "Новое КП")
        where T : class
    {
        ArgumentNullException.ThrowIfNull(cards);

        var presentation = new object[cards.Count + 1];
        for (var index = 0; index < cards.Count; index++)
            presentation[index] = cards[index] ?? throw new ArgumentException(
                "Document cards must not contain null.", nameof(cards));

        presentation[^1] = new NewContractCardItem(canCreate, caption);
        return Array.AsReadOnly(presentation);
    }
}
