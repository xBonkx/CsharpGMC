namespace GmcSmeta.Core.Domain;

/// <summary>
/// Last visual item in the object document grid, matching Python ObjectPage.NewCard.
/// This is presentation-only; document lists and financial totals stay unchanged.
/// </summary>
public sealed record NewContractCardItem(bool CanCreate);

public static class ObjectCardLayout
{
    /// <summary>
    /// Copy real document cards in their existing order and append exactly one
    /// creation item, including for an empty list. Does not mutate SQL data.
    /// </summary>
    public static IReadOnlyList<object> AppendNewContract<T>(IReadOnlyList<T> cards, bool canCreate)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(cards);

        var presentation = new object[cards.Count + 1];
        for (var index = 0; index < cards.Count; index++)
            presentation[index] = cards[index] ?? throw new ArgumentException(
                "Document cards must not contain null.", nameof(cards));

        presentation[^1] = new NewContractCardItem(canCreate);
        return Array.AsReadOnly(presentation);
    }
}
