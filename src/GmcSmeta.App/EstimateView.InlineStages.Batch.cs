using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>
/// Python stage_mode.picked() / put_rest() / take_out_all():
/// group and section selections resolve to real source position IDs;
/// no virtual IDs reach the SQL Server bulk writer.
/// </summary>
public partial class EstimateView
{
    private bool BulkStageAllowed =>
        !_detached && _inlineStagesOn && _inlineStagesData is { } snapshot && !_inlineStagesLoading && !_inlineStageCommandBusy &&
        !string.IsNullOrWhiteSpace(snapshot.Revision) && snapshot.Revision == _serverRevision &&
        !_serverConflict && System.Threading.Volatile.Read(ref _pendingWrites) == 0 &&
        new SqlConnectionStringBuilder(_sqlConnectionString).InitialCatalog
            .StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase);

    private InlineStagesSnapshot? _selectionButtonsSnapshot;

    private IReadOnlyList<long> SelectedStageSourceIds() =>
        SelectedStageSourceSet().OrderBy(id => id).ToArray();

    private HashSet<long> SelectedStageSourceSet()
    {
        var ids = new HashSet<long>();
        if (_inlineStagesData is not { } data || EstimateGrid is null)
            return ids;
        foreach (var row in EstimateGrid.SelectedItems.OfType<EditableEstimateRow>())
        {
            if (row.IsTypeHeader) continue; // type headings are not positions

            if (row.IsGroup)
            {
                foreach (var id in row.GroupMemberIds)
                    if (data.Rows.ContainsKey(checked((int)id))) ids.Add(id);
            }
            else if (row.IsSystem)
            {
                // Python picked(): selecting a system means all its real
                // top-level positions (not virtual headings or child rows).
                ids.UnionWith(data.SourcesBySystem[row.Source.SystemId]);
            }
            else if (row.IsSub && row.Source.ParentPositionId is int parent)
            {
                if (data.Rows.ContainsKey(parent)) ids.Add(parent);
            }
            else if (!row.IsVirtual && data.Rows.ContainsKey(row.Id))
            {
                ids.Add(row.Id);
            }
        }
        return ids;
    }

    private void InlineStagesGrid_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (_inlineStagesOn && InlineStagesSelectionButtons is not null)
            RefreshInlineStagesSelectionButtons();
    }

    private void RefreshInlineStagesSelectionButtons()
    {
        RefreshInlineStageCommandButtons();
        if (InlineStagesSelectionButtons is null || InlineStagesSelectionInfo is null)
            return;
        var data = _inlineStagesData;
        if (!_inlineStagesOn || data is null)
        {
            ClearInlineSelectionButtons();
            InlineStagesSelectionInfo.Text = "";
            return;
        }

        var ids = SelectedStageSourceSet();
        var editable = BulkStageAllowed;
        InlineStagesSelectionInfo.Text = !new SqlConnectionStringBuilder(_sqlConnectionString)
            .InitialCatalog.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase)
            ? "Массовое распределение пока доступно только на тестовой БД."
            : ids.Count > 0 ? $"Выделено: {ids.Count} позиций →"
                            : "Выдели позиции, группу или систему →";

        // No inactive button wall before selection; signed stages remain
        // visible in the cards and grid but are not allocation targets.
        if (ids.Count == 0)
        {
            ClearInlineSelectionButtons();
            return;
        }
        // Buttons capture only a stage ID; the command resolves current selection
        // and checks the current authoritative snapshot again on every click.
        if (ReferenceEquals(_selectionButtonsSnapshot, data))
        {
            foreach (var child in InlineStagesSelectionButtons.Children.OfType<Button>())
                child.IsEnabled = editable;
            return;
        }
        ClearInlineSelectionButtons();
        _selectionButtonsSnapshot = data;
        for (var i = 0; i < data.Stages.Count; i++)
        {
            var stage = data.Stages[i];
            if (stage.Locked) continue;
            var sourceId = stage.Id;
            var btn = new Button
            {
                Content = $"→ {ShortStageName(stage.Name)}",
                IsEnabled = editable && ids.Count > 0 && !stage.Locked,
                Style = (Style)FindResource("PythonToolbarButton"),
                MinHeight = 28, Margin = new Thickness(0, 0, 6, 2),
                Padding = new Thickness(9, 3, 9, 3),
                Background = StageBrush(StageTints[i % StageTints.Length]),
                BorderBrush = StageBrush("#D0D7E2"),
                ToolTip = stage.Locked
                    ? "Подписанный этап нельзя изменять"
                    : "Весь положительный остаток выделенных позиций → этап, одной SQL-транзакцией"
            };
            btn.Click += async (_, _) => await RunSelectedStageBatchAsync(
                StageBatchPlanner.Action.PutRemaining, sourceId);
            InlineStagesSelectionButtons.Children.Add(btn);
        }

        var remove = new Button
        {
            Content = "✕ Убрать из этапов",
            Style = (Style)FindResource("PythonToolbarButton"),
            MinHeight = 28, Margin = new Thickness(0, 0, 6, 2),
            Padding = new Thickness(9, 3, 9, 3),
            IsEnabled = editable && ids.Count > 0,
            ToolTip = "Убрать выбранные позиции из всех НЕподписанных этапов и вернуть в остаток"
        };
        remove.Click += async (_, _) => await RunSelectedStageBatchAsync(
            StageBatchPlanner.Action.ReturnSelected, null);
        InlineStagesSelectionButtons.Children.Add(remove);
    }

    private void ClearInlineSelectionButtons()
    {
        _selectionButtonsSnapshot = null;
        InlineStagesSelectionButtons.Children.Clear();
    }

    private async Task RunSelectedStageBatchAsync(StageBatchPlanner.Action action,
        long? stageId)
    {
        if (!BulkStageAllowed || _inlineStagesData is not { } data)
        {
            SetStatus("Массовые изменения доступны после проверки на тестовой GmcSmetaPilot_*.", true);
            return;
        }
        var selected = SelectedStageSourceIds();
        if (selected.Count == 0)
        {
            SetStatus("Сначала выбери реальные позиции или разделы.", true);
            return;
        }
        var expected = selected.Select(id =>
        {
            var row = data.Rows[checked((int)id)];
            return new StageBatchPlanner.Snapshot(
                row.SrcId, row.Qty, row.Rest, row.PerStage.ToArray());
        }).ToArray();
        var stageIds = data.Stages.Select(x => x.Id).ToArray();
        var index = action == StageBatchPlanner.Action.PutRemaining
            ? Array.IndexOf(stageIds, stageId ?? 0L) : -1;

        IReadOnlyList<StageBatchPlanner.Change> preview;
        try
        {
            preview = StageBatchPlanner.Plan(expected, selected, stageIds.Length,
                action, index);
        }
        catch (Exception ex)
        {
            SetStatus("Выделение этапов: " + ex.Message, true);
            return;
        }
        if (preview.Count == 0)
        {
            SetStatus(action == StageBatchPlanner.Action.PutRemaining
                ? "В выделенных строках нет положительного остатка для переноса."
                : "Выделенные строки не распределены по этапам.", false);
            return;
        }
        if (preview.Any(x => data.Stages[x.StageIndex].Locked))
        {
            SetStatus("Выделенные позиции используются в подписанном этапе. Операция отменена.", true);
            return;
        }

        var affected = preview.Select(x => x.PositionId).Distinct().Count();
        var destination = action == StageBatchPlanner.Action.PutRemaining
            ? $"«{ShortStageName(data.Stages[index].Name)}»" : "остаток";
        var text = action == StageBatchPlanner.Action.PutRemaining
            ? $"Перенести положительный остаток {affected} позиций в {destination}?"
            : $"Убрать {affected} выделенных позиций из этапов? " +
              "Само полное КП не изменится.";
        text += $"\nБудет изменено ячеек: {preview.Count}. " +
            "\nДействие выполняется одной транзакцией SQL Server " +
            "и при ошибке отменяется целиком.";
        if (MessageBox.Show(Window.GetWindow(this), text, "Распределение этапов",
                MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Freeze the preview exactly as shown; recheck the selection after
        // the modal confirmation, and pass the displayed revision to SQL.
        if (!BulkStageAllowed || !ReferenceEquals(data, _inlineStagesData) ||
            !SelectedStageSourceIds().SequenceEqual(selected))
        {
            SetStatus("Распределение или выделение изменилось. Повтори команду.", true);
            return;
        }
        var rev = data.Revision;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        System.Threading.Interlocked.Increment(ref _pendingWrites);
        RefreshInlineStagesSelectionButtons();
        SetStatus($"Сохранение этапов: {affected} позиций…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || rev != _serverRevision ||
                !ReferenceEquals(data, _inlineStagesData))
                throw new InvalidOperationException("Распределение изменилось до записи.");
            var result = await Task.Run(() => SqlServerStages.ChangeSelected(
                _sqlConnectionString, _contractId, stageId, action,
                selected, stageIds, expected, rev));
            if (!result.Saved)
            {
                _saveError = "Распределение не сохранено: " + result.Error;
                _serverConflict = true;
                return;
            }
            _serverRevision = result.Revision;
            _saveAwaitingReadback = true;
        }
        catch (Exception ex)
        {
            _serverConflict = true;
            _saveAwaitingReadback = true;
            _saveError = "Массовое распределение не подтверждено: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (System.Threading.Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError ?? "Распределение записано, выполняется сверка…", _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
            RefreshInlineStagesSelectionButtons();
        }
    }
}
