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
        var width = Math.Max(300, viewportWidth > 0 ? viewportWidth : actualWidth - 18);
        var columns = Math.Clamp((int)((width + 12) / 352), 1, 3);
        // WrapPanel includes the trailing margin of each card, including the last.
        return (columns, Math.Floor(width / columns) - 12);
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
