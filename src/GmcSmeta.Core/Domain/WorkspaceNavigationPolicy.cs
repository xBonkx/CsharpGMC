namespace GmcSmeta.Core.Domain;

/// <summary>Navigation routing and publication decisions, independent of WPF and SQL.</summary>
public static class WorkspaceNavigationPolicy
{
    public static NavigationMode ForDocument(bool archived, string kind) =>
        archived ? NavigationMode.Archive : kind == "kp" ? NavigationMode.Kp : NavigationMode.Contracts;

    public static NavigationMode ForObject(bool archived, NavigationMode current, IEnumerable<string> documentKinds)
    {
        if (archived) return NavigationMode.Archive;
        var kinds = documentKinds.ToList();
        var hasKp = kinds.Contains("kp");
        var hasContract = kinds.Any(kind => kind is "main" or "dop");
        return current == NavigationMode.Kp && hasKp ? NavigationMode.Kp
            : current == NavigationMode.Contracts && hasContract ? NavigationMode.Contracts
            : hasKp || kinds.Count == 0 ? NavigationMode.Kp : NavigationMode.Contracts;
    }

    public static bool CanPublishData(long capturedGeneration, long currentGeneration,
        string capturedConnection, string? currentConnection, bool closed) =>
        !closed && capturedGeneration == currentGeneration &&
        string.Equals(capturedConnection, currentConnection, StringComparison.Ordinal);

    public static bool CanReplaceCachedEstimate(bool canTransfer, bool unsettledWrites, bool readingOrWriting) =>
        canTransfer && !unsettledWrites && !readingOrWriting;
}
