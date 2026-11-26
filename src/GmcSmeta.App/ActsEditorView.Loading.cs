using System.Threading;
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

    private async Task ReloadAsync(long? requestedActId, bool reuseList)
    {
        if (_contractId == 0 || ActWritePending) return;
        var generation = ++_actLoadGeneration;
        using var cancellation = new CancellationTokenSource();
        var previous = _actLoadCancellation;
        _actLoadCancellation = cancellation;
        previous?.Cancel();
        var token = cancellation.Token;
        var connectionString = _connectionString;
        var contractId = _contractId;
        var requested = requestedActId ?? _actId;
        var cachedActs = reuseList ? _acts.ToList() : null;
        var cachedRevision = _actsRevision;
        _loadingAct = true;
        _loadFailed = false;
        _publishedActRevision = "";
        _revision = "";
        // Clear the old sheet before publishing the target ID. Selection remains
        // available in the surrounding contract list while this editor is hidden.
        _building = true;
        try
        {
            ShowActUnavailable(null);
            EmptyTitle.Text = "Загрузка КС-2…";
            EmptyDescription.Text = "Чтение данных из SQL Server.";
            if (_acts.Any(a => a.Id == requested)) _actId = requested;
            SyncActSelector();
        }
        finally { _building = false; }

        try
        {
            var loaded = await Task.Run(() => ReadActSnapshot(connectionString, contractId,
                requested, cachedActs, cachedRevision, token), token);
            if (token.IsCancellationRequested || generation != _actLoadGeneration || contractId != _contractId ||
                connectionString != _connectionString) return;
            _building = true;
            _acts = loaded.Acts;
            _actId = loaded.ActId;
            _actsRevision = loaded.Revision;
            _revision = loaded.Revision;
            ApplyActSnapshot(loaded);
            _publishedActId = loaded.ActId;
            _publishedActRevision = loaded.Revision;
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // A newer selection owns the UI. No write was cancelled.
        }
        catch (Exception ex)
        {
            if (generation != _actLoadGeneration) return;
            _building = true;
            _revision = "";
            _actsRevision = "";
            _loadFailed = true;
            ShowActUnavailable(ex.Message);
            SetStatus("Ошибка чтения акта: " + ex.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_actLoadCancellation, cancellation)) _actLoadCancellation = null;
            if (generation == _actLoadGeneration)
            {
                _loadingAct = false;
                _building = false;
                UpdateSelectionQty();
                ActsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}
