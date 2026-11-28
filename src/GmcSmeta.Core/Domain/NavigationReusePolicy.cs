namespace GmcSmeta.Core.Domain;

/// <summary>Value-based branch reuse; changed documents are never represented by stale node tags.</summary>
public static class NavigationReusePolicy
{
    public static bool SameBranch(NavigationObject previous, NavigationObject next) =>
        previous.Object == next.Object && previous.Documents.SequenceEqual(next.Documents);

    public static bool Expanded(string key, bool defaultExpanded,
        IReadOnlySet<string>? opened, IReadOnlySet<string>? known) =>
        known?.Contains(key) == true ? opened?.Contains(key) == true : defaultExpanded;
}
