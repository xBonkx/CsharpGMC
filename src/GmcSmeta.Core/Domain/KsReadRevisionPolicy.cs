namespace GmcSmeta.Core.Domain;

/// <summary>Revision equality for a coherent read and reuse of a confirmed read-only act.</summary>
public static class KsReadRevisionPolicy
{
    public static bool IsStable(string before, string after) =>
        !string.IsNullOrWhiteSpace(before) && string.Equals(before, after, StringComparison.Ordinal);

    public static bool CanUseCachedContext(string cachedConnection, int cachedContract, string cachedRevision,
        string connection, int contract, string revision) =>
        cachedConnection == connection && cachedContract == contract && IsStable(cachedRevision, revision);

    public static bool IsSameGeneration(long capturedRead, long capturedAttachment, long read, long attachment) =>
        capturedRead == read && capturedAttachment == attachment;

    public static bool CanReuse(string published, string displayed, string listRevision,
        string observed, long publishedId, long selectedId, bool cleanReadOnly) =>
        cleanReadOnly && publishedId == selectedId &&
        IsStable(published, displayed) && IsStable(published, listRevision) && IsStable(published, observed);
}
