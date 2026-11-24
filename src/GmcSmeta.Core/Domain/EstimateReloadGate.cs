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

    public bool CanApply(long request, int pendingWrites,
        string localRevisionAtStart, string localRevisionNow, string loadedRevision)
    {
        if (!IsLatest(request) || pendingWrites != 0) return false;
        return string.Equals(localRevisionAtStart, localRevisionNow, StringComparison.Ordinal) ||
               string.Equals(loadedRevision, localRevisionNow, StringComparison.Ordinal);
    }
}
