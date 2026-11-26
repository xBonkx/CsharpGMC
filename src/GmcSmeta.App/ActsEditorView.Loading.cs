using System.Threading;
using System.Windows;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class ActsEditorView
{
    private sealed record ActLoadSnapshot(
        List<SqlServerActs.ActInfo> Acts, long ActId, string Revision,
        SqlServerActs.ActSheetData? Sheet, SqlServerActs.ActDetails? Details,
        ContractTotals? Totals, TagRules? Rules, HashSet<long> SharedExtraIds);

    private static ActLoadSnapshot ReadActSnapshot(string connectionString, int contractId,
        long requestedId, List<SqlServerActs.ActInfo>? cachedActs, string cachedRevision,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var before = Revision();
            var acts = cachedActs is not null && !string.IsNullOrEmpty(cachedRevision) &&
                string.Equals(before, cachedRevision, StringComparison.Ordinal)
                ? cachedActs : SqlServerActs.ActsOf(conn, contractId, token: token);
            var id = KsWorkspaceProjection.ResolveActId(acts, requestedId);
            var sheet = id == 0 ? null : SqlServerActs.ActSheet(conn, id, contractId, token);
            var details = id == 0 ? null : SqlServerActs.ReadDetails(conn, id, token);
            var totals = id == 0 ? null : Totals.ReadOne(conn, contractId, token);
            var rules = id == 0 ? null : TagRules.Load(conn, token);
            var sharedExtras = sheet is not null && sheet.Positions.Any(p => p.OutOfKp)
                ? ReadSharedExtras(conn, id, token) : new HashSet<long>();
            var after = Revision();
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(before) && before == after)
                return new ActLoadSnapshot(acts, id, after, sheet, details, totals, rules, sharedExtras);
            cachedActs = null; // A concurrent change invalidates the cached list too.
        }
        throw new InvalidOperationException("КС-2 изменилась во время чтения. Повторите загрузку.");
    }

    // Only ordinary reads can be cancelled by navigation. The default protects
    // existing post-write callers, including recovery after an uncertain result.
    private bool _actLoadRequired;
    private long _activeReadActId;
    private long? _resumeActId;

    private void UpdateActReadVisibility()
    {
        if (!IsLoaded || !IsVisible) CancelHiddenActRead();
        else ResumeActReadIfNeeded();
    }

    private void CancelHiddenActRead()
    {
        if (_actLoadRequired || ActWritePending || _actLoadCancellation is not { } cancellation ||
            cancellation.IsCancellationRequested) return;
        _resumeActId = _activeReadActId;
        cancellation.Cancel();
    }

    private void ResumeActReadIfNeeded()
    {
        if (!IsLoaded || !IsVisible || _loadingAct || _building || ActWritePending ||
            _resumeActId is not long id) return;
        _ = ReloadAsync(id, reuseList: false, requiredReadback: false);
    }

    private void BeginActLoadingPresentation()
    {
        // Never expose commands for retained old rows while the revision is
        // unconfirmed. Keep the surrounding list available for another selection.
        var retain = _sheet is not null && EditorRoot.Visibility == Visibility.Visible;
        var wasBuilding = _building;
        _building = true;
        try
        {
            EditorRoot.IsEnabled = false;
            EmptyPanel.Visibility = Visibility.Collapsed;
            if (!retain) EditorRoot.Visibility = Visibility.Collapsed;
            ActLoadingPanel.VerticalAlignment = retain ? VerticalAlignment.Top : VerticalAlignment.Center;
            ActLoadingPanel.Visibility = Visibility.Visible;
        }
        finally { _building = wasBuilding; }
        SetStatus("Загрузка КС-2 из SQL Server…");
    }

    private void EndActLoadingPresentation()
    {
        ActLoadingPanel.Visibility = Visibility.Collapsed;
        EditorRoot.IsEnabled = true;
    }

    private async Task ReloadAsync(long? requestedActId, bool reuseList, bool requiredReadback = true)
    {
        if (_contractId == 0 || ActWritePending || _actLoadRequired) return;
        var requested = requestedActId ?? SelectedActId;
        if (!requiredReadback && IsLoaded && !IsVisible)
        {
            CancelHiddenActRead();
            _resumeActId = requested;
            _revision = "";
            _publishedActRevision = "";
            BeginActLoadingPresentation();
            return;
        }
        _resumeActId = null;
        _actLoadRequired = requiredReadback;
        _activeReadActId = requested;
        var generation = ++_actLoadGeneration;
        using var cancellation = new CancellationTokenSource();
        var previous = _actLoadCancellation;
        _actLoadCancellation = cancellation;
        previous?.Cancel();
        var token = requiredReadback ? CancellationToken.None : cancellation.Token;
        var connectionString = _connectionString;
        var contractId = _contractId;
        var cachedActs = reuseList ? _acts.ToList() : null;
        var cachedRevision = _actsRevision;
        _loadingAct = true;
        _loadFailed = false;
        _publishedActRevision = "";
        _revision = "";
        // Keep the published act ID and rows together until the next coherent
        // snapshot arrives. The loading banner is separate from the empty state.
        BeginActLoadingPresentation();

        try
        {
            var loaded = await Task.Run(() => ReadActSnapshot(connectionString, contractId,
                requested, cachedActs, cachedRevision, token), token);
            if (token.IsCancellationRequested || generation != _actLoadGeneration || contractId != _contractId ||
                connectionString != _connectionString) return;
            _building = true;
            ClearActSheetState();
            _acts = loaded.Acts;
            _actId = loaded.ActId;
            _actsRevision = loaded.Revision;
            _revision = loaded.Revision;
            ApplyActSnapshot(loaded);
            _publishedActId = loaded.ActId;
            _publishedActRevision = loaded.Revision;
            EndActLoadingPresentation();
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A newer selection or navigation owns the UI. No write was cancelled.
        }
        catch (Exception ex)
        {
            if (generation != _actLoadGeneration) return;
            _building = true;
            _revision = "";
            _actsRevision = "";
            _loadFailed = true;
            _actId = requested; // Retry the requested act, not the retained old one.
            ShowActUnavailable(ex.Message);
            EndActLoadingPresentation();
            SetStatus("Ошибка чтения акта: " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_actLoadCancellation, cancellation))
            {
                _actLoadCancellation = null;
                _actLoadRequired = false;
            }
            if (generation == _actLoadGeneration)
            {
                _loadingAct = false;
                _building = false;
                if (!token.IsCancellationRequested)
                {
                    UpdateSelectionQty();
                    ActsChanged?.Invoke(this, EventArgs.Empty);
                }
                ResumeActReadIfNeeded();
            }
        }
    }
}
