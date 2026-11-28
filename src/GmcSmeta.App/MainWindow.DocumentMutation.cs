using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // Navigation totals are intentionally absent from metadata-only SQL reads.
    private static DocRow DocumentMetadata(DocRow row) =>
        row with { Materials = 0m, Smr = 0m, Total = 0m, Positions = 0 };

    private bool DocumentMatchesServer(DocRow expected, string revision)
    {
        if (_serverConn is null || string.IsNullOrWhiteSpace(revision) ||
            SqlServerDatabase.Revision(_serverConn) != revision) return false;
        var actual = Workspace.ReadDocumentMetadata(_serverConn, expected.Id);
        return actual is not null && Equals(DocumentMetadata(expected), DocumentMetadata(actual)) &&
            SqlServerDatabase.Revision(_serverConn) == revision;
    }

    private bool DocumentLocallyCurrent(DocRow expected) =>
        _docs.Any(row => Equals(DocumentMetadata(row), DocumentMetadata(expected)));

    // Opening legacy editing workspaces must not be a back door from a
    // read-only connection, archived card, plain KP or generated stage.
    private bool CanOpenContractWorkspace(DocRow expected)
    {
        if (!SqlWritesEnabled || (expected.Kind is not ("main" or "dop")) ||
            expected.IsStage || expected.StageRest || !CanStartDocumentMutation(expected) ||
            _objects.FirstOrDefault(o => o.Id == expected.ObjectId && !o.Archived) is not { } parent ||
            CaptureObjectMutation(parent) is not { } target ||
            !DocumentMatchesServer(expected, target.Revision))
        {
            StatusText.Text = "Рабочая вкладка недоступна: проверьте тип документа, архив и сохранение.";
            return false;
        }
        return true;
    }

    internal bool IsCurrentEstimatePage(int documentId, int objectId, EstimateView page) =>
        !_startupClosed && _openDocumentId == documentId &&
        ReferenceEquals(PageHost.Content, page) &&
        _docs.Any(d => d.Id == documentId && d.ObjectId == objectId);

    internal bool CanEditEstimateHeader(int documentId, int objectId) =>
        SqlWritesEnabled && !_startupClosed && _openDocumentId == documentId &&
        _objects.Any(o => o.Id == objectId && !o.Archived) &&
        _docs.Any(d => d.Id == documentId && d.ObjectId == objectId && !d.StageRest);

    internal bool CanReceiveSystemCopy(DocRow target, int sourceId, int objectId) =>
        CanEditEstimateHeader(sourceId, objectId) && SystemCopySource.CanReceive(target, objectId) &&
        DocumentLocallyCurrent(target) && CanStartDocumentMutation(target);

    internal void RefreshSystemCopyTarget(int targetId)
    {
        if (_estimateCache.TryGetValue(targetId, out var cached)) cached.RequestRefresh();
        ReloadNavigationData();
    }

    private bool CanStartDocumentMutation(DocRow expected) =>
        !_estimateCache.TryGetValue(expected.Id, out var cached) || cached.CanTransferDocument;
}
