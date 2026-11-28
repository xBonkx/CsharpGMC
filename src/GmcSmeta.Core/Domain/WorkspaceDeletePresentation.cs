namespace GmcSmeta.Core.Domain;

/// <summary>Read-only warning text. The SQL writer independently enforces deletion safety.</summary>
public static class WorkspaceDeletePresentation
{
    public static IReadOnlyList<string> ContractDetails(int acts, int movements)
    {
        var details = new List<string>();
        if (acts > 0) details.Add($"акты КС-2/КС-3: {acts}");
        if (movements > 0) details.Add($"история закупок и выдачи: {movements} записей");
        return details;
    }

    public static string? DocumentDetails(IReadOnlyList<string> titles) =>
        titles.Count > 0
            ? "документы: " + titles.Count + " (" + string.Join(", ", titles.Take(4))
                + (titles.Count > 4 ? " …" : "") + ")"
            : null;

    public static string ActDetails(long acts, long signed) =>
        "акты КС-2/КС-3: " + acts + (signed > 0 ? $", из них подписанных: {signed}" : "");
}
