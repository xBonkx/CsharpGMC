using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python kp_editor._build_view("same"/"type") presentation: synthetic header
/// rows exist only in WPF. Their negative IDs are never written to SQL Server.
/// </summary>
public partial class EstimateView
{
    private string _temporaryGroupMode = EstimateViewProjection.DocumentOrder;
    private readonly HashSet<string> _expandedGroupKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _collapsedTypeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _frozenGroupedOrder = new(StringComparer.Ordinal);
    private EstimateGroups.Prepared? _preparedGroups;

    private bool ApplyGroupedFilter(bool expandAllGroups)
    {
        if (_temporaryGroupMode is not (EstimateGroups.Same or EstimateGroups.Type) ||
            _estimate is null)
        {
            _preparedGroups = null;
            return false;
        }

        using var timing = GmcSmeta.Core.StartupTrace.Measure("estimate_ui_group_projection");
        if (_preparedGroups is null || !ReferenceEquals(_preparedGroups.Rows, _estimate.Rows))
            _preparedGroups = EstimateGroups.Prepare(_estimate.Rows);
        var sections = EstimateGroups.BuildPrepared(_preparedGroups, _temporaryGroupMode,
            _temporarySort, EstimateSearch.Text, _noPriceTemporaryView,
            _frozenGroupedOrder, SearchIndexFor(_estimate.Rows));
        // Expand all uses this very projection; do not build groups twice.
        if (expandAllGroups)
            foreach (var group in sections.SelectMany(section => section.Groups))
                if (group.Members.Count > 1)
                    _expandedGroupKeys.Add(group.Key);

        var index = RowViewIndex;
        var byId = index.ByPositionId;
        var projected = new List<EditableEstimateRow>(_rows.Count);
        var virtualKey = -1;
        var showPositionNumbers = EstimateViewProjection.SortField(_temporarySort) == EstimateViewProjection.ByNumber;
        var revealNested = !string.IsNullOrWhiteSpace(EstimateSearch.Text) ||
            (_inlineStagesOn && _inlineStagesFilter != StageAllocationPresentation.All);
        var typeMode = _temporaryGroupMode == EstimateGroups.Type;

        void ShowPosition(EstimateRow source, int depth)
        {
            var original = byId[source.Id];
            var number = showPositionNumbers
                ? original.Num : index.SectionName(source.SystemId, original.Num);
            if (original.DisplayNum != number) original.DisplayNum = number;
            if (original.DisplayIndentLevel != depth) original.DisplayIndentLevel = depth;
            projected.Add(original);
        }

        foreach (var section in sections)
        {
            if (typeMode)
            {
                var typeHeader = new EditableEstimateRow(new EstimateRow(
                    RowKind.System, 0, virtualKey, virtualKey, false, "",
                    section.Title, "", 0, 0, section.Amount, null, false, ""))
                {
                    IsTypeHeader = true,
                    TypeKey = section.TypeKey,
                    IsCollapsed = _collapsedTypeKeys.Contains(section.TypeKey)
                };
                virtualKey--;
                projected.Add(typeHeader);
                if (typeHeader.IsCollapsed) continue;
            }

            foreach (var group in section.Groups)
            {
                if (_temporarySort != EstimateViewProjection.DocumentOrder &&
                    !_frozenGroupedOrder.ContainsKey(group.Key))
                    _frozenGroupedOrder.Add(group.Key, _frozenGroupedOrder.Count);

                var depth = typeMode ? 1 : 0;
                if (group.Members.Count <= 1)
                {
                    var only = group.Members[0];
                    ShowPosition(only, depth);
                    if (EstimateNestedVisibility.ShowComponents(only.IsAreaSummary,
                        _expandedAreaSummaries.Contains(only.Id), _ordinaryComponentsCollapsed, revealNested, honorAreaFold: true))
                        foreach (var child in group.Components)
                            ShowPosition(child, depth + 1);
                    continue;
                }

                var systems = group.Members
                    .Select(p => index.SectionName(p.SystemId, ""))
                    .Where(label => label.Length > 0)
                    .Distinct(StringComparer.Ordinal).Take(9).ToArray();
                var note = $"{group.Members.Count} строк: " +
                    string.Join(", ", systems.Take(8)) +
                    (systems.Length > 8 ? " …" : "");

                var display = new EditableEstimateRow(new EstimateRow(
                    RowKind.Position, depth, virtualKey, virtualKey, false, "",
                    group.Name, group.Unit, group.Quantity, group.CommonPrice ?? 0m,
                    group.Amount, group.CommonPurchase, false, note)
                    { WorkPrice = group.CommonWork })
                {
                    GroupKey = group.Key,
                    GroupMemberIds = group.Members.Select(p => (long)p.Id).ToArray(),
                    GroupHasNoPrice = group.MissingPrice,
                    IsCollapsed = !_expandedGroupKeys.Contains(group.Key),
                    DisplayNum = "",
                    DisplayIndentLevel = depth
                };
                virtualKey--;
                if (group.CommonPrice is null) display.PriceText = "разные";
                if (group.MixedPurchase) display.PurchaseText = "разные";
                if (group.MixedWork) display.WorkText = "разные";
                projected.Add(display);
                if (display.IsCollapsed) continue;

                // Collapsed groups never need a component lookup.
                var components = group.Components.Count == 0 ? null
                    : group.Components.ToLookup(x => x.ParentPositionId);
                foreach (var member in group.Members)
                {
                    ShowPosition(member, depth + 1);
                    if (EstimateNestedVisibility.ShowComponents(member.IsAreaSummary,
                        _expandedAreaSummaries.Contains(member.Id), _ordinaryComponentsCollapsed, revealNested, honorAreaFold: true))
                        foreach (var child in components?[member.Id] ?? Enumerable.Empty<EstimateRow>())
                            ShowPosition(child, depth + 2);
                }
            }
        }

        projected = ApplyInlineStagesFilter(projected);
        timing?.Dispose();
        using (GmcSmeta.Core.StartupTrace.Measure("estimate_ui_group_items_source"))
            EstimateGridRowUpdates.PublishFull(EstimateGrid, projected, observable: false);

        // Materials has its own SQL snapshot, filter and row type.
        // Never publish the estimate's temporary projection to that grid.

        RefreshTemporaryViewControls();
        return true;
    }

    /// <summary>
    /// True only for a virtual group row. SQL Server checks target IDs again
    /// under SERIALIZABLE and rejects changed group composition.
    /// </summary>
    private async void QueueGroupSave(EditableEstimateRow group, string field, string input)
    {
        _reloadTimer.Stop();
        var count = Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Групповое сохранение ({count})…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict)
            {
                _saveError ??= "Данные на сервере изменились. Обнови смету.";
                return;
            }
            var ids = group.GroupMemberIds.ToArray();
            var key = group.GroupKey;
            var result = await Task.Run(() => SqlServerDatabase.SaveGroup(
                _sqlConnectionString, _contractId, key, ids,
                field, input, _serverRevision));
            if (!result.Saved)
            {
                _saveError = result.Error ?? "Не удалось сохранить группу";
                _serverConflict = true;
            }
            else
            {
                _serverRevision = result.Revision;
                SetStatus($"{field}: обновлено {result.Changed} одинаковых строк", false);
            }
        }
        catch (Exception ex)
        {
            _saveError = ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError is null ? "Группа сохранена ✓" : "Ошибка: " + _saveError,
                    _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }
}
