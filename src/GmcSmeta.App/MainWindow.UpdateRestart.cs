using System.Windows;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // Reuse the document transfer/eviction safety rules for all cached views,
    // not only the active tab. A pending edit, COMMIT/readback, nested editor
    // operation or blocked save must postpone an automatic restart.
    internal bool CanRestartForUpdate =>
        _ready && !_startupClosed && !_startupInProgress && IsEnabled &&
        ObjectRenameBox.Visibility != Visibility.Visible &&
        _estimateCacheValidation is null &&
        _estimateCache.Values.All(view => view.CanRestartAfterUpdate) &&
        (PageHost.Content is not EstimateView current || current.CanRestartAfterUpdate);

    internal int? RestartDocumentId => PageHost.Content is EstimateView ? _openDocumentId : null;
}

public partial class EstimateView
{
    // An unresolved revision conflict must never cause an unattended close.
    internal bool CanRestartAfterUpdate => CanTransferDocument && !_serverConflict;
}
