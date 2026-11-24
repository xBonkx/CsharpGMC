namespace GmcSmeta.Core.Domain;

/// <summary>
/// Undo history belongs to the client whose writes generated its checkpoints.
/// A fresh SQL meta.rev not reported by this client invalidates the whole stack,
/// even if the document's values later appear identical.
/// </summary>
public static class EstimateUndoRevisionPolicy
{
    public static bool IsForeignRevision(string? lastKnownRevision, string? observedRevision) =>
        !string.IsNullOrEmpty(lastKnownRevision) &&
        !string.Equals(lastKnownRevision, observedRevision, StringComparison.Ordinal);
}
