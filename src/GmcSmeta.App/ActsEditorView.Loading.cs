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

    internal sealed record ActReadContext(string ConnectionString, int ContractId, string Revision,
        IReadOnlyList<SqlServerActs.ActInfo> Acts, ContractTotals? Totals, TagRules? Rules);

    private ActReadContext? _actReadContext;

    internal ActReadContext? CaptureReadContext(string connectionString, int contractId) =>
        _actReadContext is { } cached && cached.ConnectionString == connectionString &&
        cached.ContractId == contractId && !string.IsNullOrWhiteSpace(cached.Revision) ? cached : null;

    private void RememberReadContext(ActLoadSnapshot snapshot) =>
        _actReadContext = new(_connectionString, _contractId, snapshot.Revision,
            Array.AsReadOnly(snapshot.Acts.ToArray()), snapshot.Totals, snapshot.Rules);

    private static ActLoadSnapshot ReadActSnapshot(string connectionString, int contractId,
        long requestedId, List<SqlServerActs.ActInfo>? cachedActs, string cachedRevision,
        CancellationToken token = default, ActReadContext? cachedContext = null)
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
            ContractTotals? totals = null;
            TagRules? rules = null;
            if (id != 0)
            {
                if (cachedContext is { Totals: not null, Rules: not null } common &&
                    common.ConnectionString == connectionString && common.ContractId == contractId &&
                    common.Revision == before && !string.IsNullOrWhiteSpace(before))
                {
                    totals = common.Totals;
                    rules = common.Rules;
                }
                else (totals, rules) = Totals.ReadOneWithRules(conn, contractId, token);
            }
            var sheet = id == 0 ? null : SqlServerActs.ActSheet(conn, id, contractId, token, rules, totals?.Form);
            var details = id == 0 ? null : SqlServerActs.ReadDetails(conn, id, token);
            var sharedExtras = sheet is not null && sheet.Positions.Any(p => p.OutOfKp)
                ? ReadSharedExtras(conn, id, token) : new HashSet<long>();
            var after = Revision();
            token.ThrowIfCancellationRequested();
            if (KsReadRevisionPolicy.IsStable(before, after))
                return new ActLoadSnapshot(acts, id, after, sheet, details, totals, rules, sharedExtras);
            cachedActs = null; // A concurrent change invalidates the cached list too.
            cachedContext = null;
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
        if (!IsLoaded || !IsVisible)
        {
            _actExportCancellation?.Cancel();
            CancelHiddenActRead();
        }
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
        _ = ReloadAsync(id, reuseList: true, requiredReadback: false);
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
        _actExportCancellation?.Cancel();
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
        var cachedContext = reuseList ? CaptureReadContext(connectionString, contractId) : null;
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
                requested, cachedActs, cachedRevision, token, cachedContext), token);
            if (token.IsCancellationRequested || generation != _actLoadGeneration || contractId != _contractId ||
                connectionString != _connectionString) return;
            _building = true;
            ClearActSheetState();
            _acts = loaded.Acts;
            _actId = loaded.ActId;
            _actsRevision = loaded.Revision;
            _revision = loaded.Revision;
            ApplyActSnapshot(loaded);
            RememberReadContext(loaded);
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
            _actReadContext = null;
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
