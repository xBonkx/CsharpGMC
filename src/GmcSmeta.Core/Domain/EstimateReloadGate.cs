using System.Threading;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure, thread-safe generation guard for slow SQL Server document reloads.
/// A snapshot may be applied only when it represents the latest requested
/// refresh and was not overtaken by a local write while being loaded.
/// Remote changes may be accepted when no local write intervened.
/// </summary>
public sealed class EstimateReloadGate
{
    private long _latestRequest;

    public long NextRequest() => Interlocked.Increment(ref _latestRequest);

    public bool IsLatest(long request) =>
        request > 0 && request == Interlocked.Read(ref _latestRequest);

    /// <summary>
    /// A clean tab may reuse an actually published snapshot, never merely a
    /// receipt from a more recent SQL write. Missing revisions fail closed.
    /// The caller also checks editor activity and request generation.
    /// </summary>
    public static bool CanReusePublishedRevision(string publishedRevision,
        string localRevision, string observedRevision, bool requiresReadback) =>
        !requiresReadback && !string.IsNullOrWhiteSpace(publishedRevision) &&
        string.Equals(publishedRevision, localRevision, StringComparison.Ordinal) &&
        string.Equals(publishedRevision, observedRevision, StringComparison.Ordinal);

    public static bool CanCoalesceOrdinaryRead(bool allowUnchanged, bool readActive,
        bool unsettledWrites, bool activeEdit, bool reloadAfterAttach) =>
        allowUnchanged && readActive && !unsettledWrites && !activeEdit && !reloadAfterAttach;

    public bool CanApply(long request, int pendingWrites,
        string localRevisionAtStart, string localRevisionNow, string loadedRevision)
    {
        if (!IsLatest(request) || pendingWrites != 0 || string.IsNullOrWhiteSpace(loadedRevision)) return false;
        // Even a matching loaded revision cannot prove that ancillary data
        // (for example the lazy Undo checkpoint) was requested after the write.
        // Re-read once using the latest local state rather than settling it early.
        return string.Equals(localRevisionAtStart, localRevisionNow, StringComparison.Ordinal);
    }
}
