using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ShipmentsPane
{
    private CancellationTokenSource? _ordinaryShipmentCancellation;
    private long _activeShipmentReadId;
    private long? _resumeShipmentId;

    private void CancelOrdinaryShipmentRead()
    {
        if (_quantityBusy || _ordinaryShipmentCancellation is not { } cancellation) return;
        _ordinaryShipmentCancellation = null;
        _resumeShipmentId = _activeShipmentReadId;
        _needsReloadAfterAttach = true;
        ++_loadGeneration;
        _building = false;
        IsEnabled = true;
        cancellation.Cancel();
    }

    private void ResumeShipmentReadIfNeeded()
    {
        if (!IsLoaded || !IsVisible || _quantityBusy || _building || !_needsReloadAfterAttach) return;
        _ = ReloadAsync(requestedId: _resumeShipmentId, requiredReadback: false);
    }

    private sealed record ObjectSnapshot(string Revision,
        List<SqlServerShipments.ShipmentInfo> Shipments,
        List<SqlServerShipments.ShipmentDocument> Documents,
        List<SqlServerShipments.IssuableRow> Candidates);

    private sealed record ShipmentSnapshot(ObjectSnapshot Object, long ShipmentId,
        List<ShipmentPreviewRow> Rows, int LineCount);

    // Only plain data leaves the worker. No WPF objects or control state is
    // read there; the displayed revision is assigned only after publishing.
    private ShipmentSnapshot? ReadSnapshot(long requestedId, long editingFixedId,
        ObjectSnapshot? cached, string reusableRevision, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var conn = token.CanBeCanceled
            ? SqlServerDatabase.OpenAsync(_connectionString, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Open(_connectionString);
        string Revision() => token.CanBeCanceled
            ? SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult()
            : SqlServerDatabase.Revision(conn);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var revision = Revision();
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(revision))
                throw new InvalidOperationException("Отсутствует ревизия SQL Server.");
            // Null means the complete published view is still current, not
            // merely that the object-wide candidates can be reused.
            if (attempt == 0 && !string.IsNullOrWhiteSpace(reusableRevision) &&
                string.Equals(reusableRevision, revision, StringComparison.Ordinal))
                return null;
            var obj = cached is not null && cached.Revision == revision ? cached :
                ReadObjectSnapshot(conn, revision, token);
            var current = obj.Shipments.FirstOrDefault(s => s.Id == requestedId) ??
                obj.Shipments.LastOrDefault();
            var rows = new List<ShipmentPreviewRow>(current is null ? 0 : obj.Candidates.Count);
            var lineCount = 0;
            if (current is not null)
            {
                var lines = SqlServerShipments.LinesOf(conn, current.Id, token: token);
                lineCount = lines.Count;
                var byId = lines.ToDictionary(l => l.PositionId);
                var documentTitles = obj.Documents.ToDictionary(d => d.Id, d => d.Title);
                var multipleDocs = obj.Documents.Count > 1;
                var editable = !current.Fixed || current.Id == editingFixedId;
                foreach (var row in obj.Candidates)
                {
                    token.ThrowIfCancellationRequested();
                    byId.TryGetValue(row.PositionId, out var line);
                    var label = row.SystemName + (multipleDocs && documentTitles.TryGetValue(row.ContractId, out var title)
                        ? $" · {title}" : "");
                    rows.Add(BuildPreviewRow(row, line, current, editable, label));
                }
                // Historical lines remain visible even if the current
                // issuable query no longer contains their positions.
                if (lines.Count > 0)
                {
                    var known = rows.Select(r => r.PositionId).ToHashSet();
                    foreach (var line in lines.Where(l => !known.Contains(l.PositionId)))
                    {
                        token.ThrowIfCancellationRequested();
                        rows.Add(new ShipmentPreviewRow
                        {
                            PositionId = line.PositionId, Name = line.Name, Unit = line.Unit,
                            SystemLabel = "Прочие строки ведомости", ByEstimate = Num(line.QtyByKp),
                            Qty = Num(line.Qty), IssuedTotal = Num(line.Issued - (current.Fixed ? line.Qty : 0m)),
                            InShipment = true, CanChange = false,
                        });
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            var confirmed = Revision();
            token.ThrowIfCancellationRequested();
            if (confirmed == revision)
                return new ShipmentSnapshot(obj, current?.Id ?? 0, rows, lineCount);
            cached = null; // retry once, including all object-level data
        }
        throw new InvalidOperationException("Данные выдачи изменились при чтении. Обновите ведомости.");
    }

    private ObjectSnapshot ReadObjectSnapshot(Microsoft.Data.SqlClient.SqlConnection conn, string revision,
        CancellationToken token)
    {
        var shipments = SqlServerShipments.ShipmentsOf(conn, _objectId, token: token);
        // Avoid scanning the entire object when no shipment exists yet.
        var documents = shipments.Count == 0 ? new List<SqlServerShipments.ShipmentDocument>() :
            SqlServerShipments.DocumentsOf(conn, _objectId, token);
        var candidates = shipments.Count == 0 ? new List<SqlServerShipments.IssuableRow>() :
            SqlServerShipments.IssuableRows(conn, _objectId, token: token);
        return new ObjectSnapshot(revision, shipments, documents, candidates);
    }

    private async Task ReloadAsync(bool preserveStatus = false, long? requestedId = null,
        bool reuseObject = false, bool allowUnchanged = false, bool requiredReadback = true)
    {
        if (!IsLoaded || _building) return;
        var targetId = requestedId ?? _resumeShipmentId ?? _shipmentId;
        if (!requiredReadback && !IsVisible)
        {
            _resumeShipmentId = targetId;
            _needsReloadAfterAttach = true;
            return;
        }
        _resumeShipmentId = null;
        _activeShipmentReadId = targetId;
        // The default protects the existing post-write readback path.
        using var cancellation = requiredReadback ? null : new CancellationTokenSource();
        _ordinaryShipmentCancellation = cancellation;
        var token = cancellation?.Token ?? CancellationToken.None;
        var generation = ++_loadGeneration;
        var previousMessage = FooterText.Text;
        var gridState = targetId == _shipmentId && _revision.Length > 0 ? CaptureGridState() : null;
        var editingFixedId = _editingFixedId;
        var cached = reuseObject ? _objectSnapshot : null;
        var reusableRevision = allowUnchanged && CanReuseShipmentSnapshot(
            _objectSnapshot?.Revision, _revision, _revision, _shipmentId, targetId,
            !_shipmentSnapshotMayReuse || _needsReloadAfterAttach || _quantityBusy ||
            editingFixedId != 0 || (Current?.Id ?? 0) != _shipmentId || HasHeaderDraft() ||
            LinesGrid.ItemsSource is ListCollectionView { IsEditingItem: true })
            ? _revision : "";
        _shipmentSnapshotMayReuse = false;
        _revision = string.Empty;
        _building = true;
        IsEnabled = false;
        // Do not cancel a queued viewport restoration for a revision-only hit.
        if (reusableRevision.Length == 0) ++_viewportGeneration;
        SetStatus("Загрузка ведомости из SQL Server…");
        try
        {
            StartupTrace.Mark("shipment_load_begin");
            var snapshot = await Task.Run(() => ReadSnapshot(targetId, editingFixedId, cached, reusableRevision, token), token);
            if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;
            if (snapshot is null)
            {
                if (!CanReuseShipmentSnapshot(_objectSnapshot?.Revision, reusableRevision,
                    reusableRevision, _shipmentId, targetId,
                    _needsReloadAfterAttach || _quantityBusy || _editingFixedId != 0 ||
                    (Current?.Id ?? 0) != _shipmentId || HasHeaderDraft() ||
                    LinesGrid.ItemsSource is ListCollectionView { IsEditingItem: true }))
                {
                    SetStatus("Данные ведомости требуют сверки. Нажмите «Обновить».");
                    return;
                }
                _revision = reusableRevision;
                _shipmentSnapshotMayReuse = true;
                FooterText.Text = previousMessage;
                StartupTrace.Mark("shipment_tab_revision_hit");
                return;
            }
            if (reusableRevision.Length > 0) ++_viewportGeneration;
            _shipments = snapshot.Object.Shipments.ToList();
            if (!ShipmentCombo.Items.OfType<ShipmentItem>().Select(i => i.Info).SequenceEqual(_shipments))
            {
                ShipmentCombo.Items.Clear();
                foreach (var sh in _shipments)
                    ShipmentCombo.Items.Add(new ShipmentItem(sh.Id, sh));
            }
            ShipmentCombo.SelectedIndex = _shipments.FindIndex(s => s.Id == snapshot.ShipmentId);
            if (Current is not null)
                ApplyShipment(snapshot, snapshot.ShipmentId == targetId ? gridState : null);
            else
                ClearShipment("Ведомостей пока нет — создайте «+ Ведомость».");
            _objectSnapshot = snapshot.Object;
            _revision = snapshot.Object.Revision;
            _shipmentSnapshotMayReuse = true;
            _needsReloadAfterAttach = false;
            NewShipmentButton.IsEnabled = true;
            if (preserveStatus && !string.IsNullOrWhiteSpace(previousMessage))
                FooterText.Text = previousMessage;
            StartupTrace.Mark("shipment_load_end");
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;
            _objectSnapshot = null;
            _revision = "";
            ClearShipment("Не удалось загрузить выдачу: " + ex.Message + " · Нажмите «Обновить».");
            // Retain requested ID for retry; never leave old rows under a new header.
            _shipmentId = targetId;
            NewShipmentButton.IsEnabled = false;
            StartupTrace.Mark("shipment_load_failed");
        }
        finally
        {
            if (ReferenceEquals(_ordinaryShipmentCancellation, cancellation)) _ordinaryShipmentCancellation = null;
            if (generation == _loadGeneration)
            {
                _building = false;
                if (!_quantityBusy) IsEnabled = true;
            }
        }
    }

}
