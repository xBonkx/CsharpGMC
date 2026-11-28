using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Python ui/stage_mode.py: one estimate grid, additional stage/remaining
/// columns, cards and segmented filters. Stage numbers are read from their
/// own SQL Server documents. Full-KP rows are never overwritten.
/// </summary>
public partial class EstimateView
{
    private sealed record InlineStagesSnapshot(
        List<SqlServerStages.StageInfo> Stages,
        Dictionary<int, SqlServerStages.StageTableRow> Rows,
        SqlServerStages.StageInfo? Rest,
        Dictionary<long, decimal> DocumentTotals, string Revision,
        ILookup<long, long> SourcesBySystem, StageAllocationPresentation.Metrics Metrics);

    private bool _inlineStagesOn;
    private bool _requestInlineStages;
    private bool _revealInlineStageColumns;
    private bool _inlineStageCommandBusy;
    private bool _inlineStagesLoading;
    private int _inlineStagesVersion;
    private CancellationTokenSource? _inlineStagesReadCancellation;
    private readonly SemaphoreSlim _inlineStagesReadGate = new(1, 1);
    private InlineStagesSnapshot? _inlineStagesData;
    private string _inlineStagesFilter = StageAllocationPresentation.All;
    private readonly List<DataGridColumn> _inlineStageColumns = new();
    // Display-only preferences for this activation, keyed by stable stage ID
    // (zero denotes the computed remainder, not a document).
    private readonly HashSet<long> _hiddenInlineStageIds = new();
    private readonly Dictionary<DataGridColumn, long> _inlineStageIds = new();

    private bool SetInlineStageColumnVisibility(DataGridColumn column, bool visible)
    {
        if (!_inlineStageIds.TryGetValue(column, out var id)) return false;
        if (visible) _hiddenInlineStageIds.Remove(id);
        else _hiddenInlineStageIds.Add(id);
        column.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RequestEstimateColumnFit();
        return true;
    }
    private readonly record struct StageColumnKey(long Id, string Title, bool Locked);
    private StageColumnKey[] _inlineStageColumnKeys = Array.Empty<StageColumnKey>();
    private IReadOnlyList<string> _blankStageLabels = Array.Empty<string>();

    private void ToggleInlineStages_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineStageCommandBusy) return;
        if (_inlineStagesOn) { DisableInlineStages(); return; }
        RequestInlineStages();
    }

    internal void RequestInlineStages()
    {
        if (!_isKp || _detached) return;
        _requestInlineStages = true;
        _revealInlineStageColumns = true;
        if (_estimate is not null) ApplyRequestedInlineStages();
    }

    private void ApplyRequestedInlineStages()
    {
        if (!_isKp || _estimate is null || _detached) return;
        var requested = _requestInlineStages;
        var remembered = (_uiSettings.StageModeKp ?? new List<int>()).Contains(_contractId);
        if (!requested && !remembered) return;
        _requestInlineStages = false;
        if (requested) EstimateModeTab.IsChecked = true;
        if (!_inlineStagesOn) _ = EnableInlineStagesAsync();
        else if (requested && _inlineStagesData is { } snapshot)
            RevealInlineStageColumns(snapshot);
    }

    private async Task EnableInlineStagesAsync()
    {
        if (!_isKp || _inlineStagesOn || _estimate is null || _detached) return;
        _inlineStagesOn = true;
        InlineStagesBar.Visibility = Visibility.Visible;
        // A superseded or failed read must not turn off a newer mode request.
        // Leave the visible error and allow F5 or an explicit off/on retry.
        var stored = _uiSettings.StageModeKp ?? new List<int>();
        if (!stored.Contains(_contractId)) stored.Add(_contractId);
        _uiSettings.StageModeKp = stored;
        SaveEstimateUiSettings();
        await RefreshInlineStagesAsync();
    }

    private void CloseInlineStages_Click(object sender, RoutedEventArgs e)
    {
        if (!_inlineStageCommandBusy) DisableInlineStages();
    }

    private void DisableInlineStages()
    {
        _inlineStagesOn = false;
        _requestInlineStages = false;
        _revealInlineStageColumns = false;
        _inlineStagesLoading = false;
        _inlineStagesVersion++; // discard any read finishing after exit
        _inlineStagesReadCancellation?.Cancel();
        _inlineStagesData = null;
        _inlineStagesFilter = StageAllocationPresentation.All;
        if (InlineStagesFilter is not null) InlineStagesFilter.SelectedIndex = 0;
        if (InlineStagesBar is not null) InlineStagesBar.Visibility = Visibility.Collapsed;
        foreach (var column in _inlineStageColumns)
            EstimateGrid.Columns.Remove(column);
        _inlineStageColumns.Clear();
        _inlineStageIds.Clear();
        _hiddenInlineStageIds.Clear();
        _inlineStageColumnKeys = Array.Empty<StageColumnKey>();
        _blankStageLabels = Array.Empty<string>();
        RefreshInlineStagesSelectionButtons();
        InlineStagesCards.Children.Clear();
        foreach (var row in _rows)
        {
            row.StageDisplay = Array.Empty<string>();
            row.StageOverrun = false;
        }
        if (_estimate is not null) ApplyFilter();
        var stored = _uiSettings.StageModeKp ?? new List<int>();
        stored.Remove(_contractId);
        _uiSettings.StageModeKp = stored;
        SaveEstimateUiSettings();
    }

    private async Task<bool> RefreshInlineStagesAsync()
    {
        if (!_inlineStagesOn || _estimate is null || _detached) return false;
        var version = ++_inlineStagesVersion;
        _inlineStagesReadCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _inlineStagesReadCancellation = cancellation;
        var token = cancellation.Token;
        _inlineStagesLoading = true;
        _inlineStagesData = null; // No stale allocation may remain writable after a failed read.
        RefreshInlineStagesSelectionButtons();
        InlineStagesSummary.Text = "Читаю распределение из SQL Server…";
        try
        {
            var data = await Task.Run(async () =>
            {
                await _inlineStagesReadGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    using var conn = await SqlServerDatabase.OpenAsync(_sqlConnectionString, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    var revisionBefore = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                    var full = await SqlServerStages.FullOfAsync(conn, _contractId, token).ConfigureAwait(false);
                    if (full != _contractId)
                        throw new InvalidOperationException("Это КП этапа. Режим распределения открывается в полном КП.");
                    token.ThrowIfCancellationRequested();
                    var (stages, rows, rest) = SqlServerStages.ReadStageDistribution(conn, full, token);
                    token.ThrowIfCancellationRequested();

                    // Scope the shared totals calculation to this KP's stage documents.
                    // The enclosing revision fence covers every bounded batch.
                    var stageIds = stages.Select(s => checked((int)s.Id));
                    if (rest is not null) stageIds = stageIds.Append(checked((int)rest.Id));
                    var allTotals = Totals.ReadSelected(conn, stageIds, token)
                        .ToDictionary(t => (long)t.Id, t => t.Total);
                    var totals = new Dictionary<long, decimal>();
                    foreach (var stage in stages)
                        totals[stage.Id] = allTotals.GetValueOrDefault(stage.Id);
                    if (rest is not null)
                        totals[rest.Id] = allTotals.GetValueOrDefault(rest.Id);

                    if (string.IsNullOrWhiteSpace(revisionBefore) || revisionBefore != await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false))
                        throw new InvalidOperationException(
                            "Распределение изменилось во время чтения. Обнови смету.");
                    token.ThrowIfCancellationRequested();
                    var index = StageAllocationPresentation.IndexSourcesBySystem(rows.Select(r => (r.SystemId, r.SrcId)));
                    var metrics = StageAllocationPresentation.Summarize(stages.Count,
                        rows.Select(r => (r.Price, r.Rest, (IReadOnlyList<decimal>)r.PerStage)));
                    token.ThrowIfCancellationRequested();
                    return new InlineStagesSnapshot(stages,
                        rows.ToDictionary(r => checked((int)r.SrcId)),
                        rest, totals, revisionBefore, index, metrics);
                }
                finally { _inlineStagesReadGate.Release(); }
            }, token);
            if (!_inlineStagesOn || _detached || token.IsCancellationRequested || version != _inlineStagesVersion) return false;
            if (data.Revision != _serverRevision)
            {
                InlineStagesSummary.Text = "Смета изменилась, обновляем распределение…";
                RequestRefresh();
                return false;
            }
            _inlineStagesData = data;
            _blankStageLabels = Array.AsReadOnly(
                Enumerable.Repeat("", data.Stages.Count + 1).ToArray());
            RebuildInlineStageColumns();
            DrawInlineStageCards();
            ApplyFilter();
            if (_revealInlineStageColumns) RevealInlineStageColumns(data);
            return true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            if (_inlineStagesOn && !_detached && !token.IsCancellationRequested && version == _inlineStagesVersion)
            {
                SetStatus("Этапы: " + ex.Message, true);
                InlineStagesSummary.Text = "Ошибка чтения этапов: " + ex.Message;
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_inlineStagesReadCancellation, cancellation)) _inlineStagesReadCancellation = null;
            if (version == _inlineStagesVersion)
            {
                _inlineStagesLoading = false;
                if (!_detached) RefreshInlineStagesSelectionButtons();
            }
        }
    }

    private static readonly string[] StageTints =
    [ "#E8F0FF", "#EAF7EE", "#F4ECFF", "#FFF4E0", "#E6F6F8", "#FBEAF0" ];
    private static readonly string[] StageInk =
    [ "#1D4ED8", "#15803D", "#7E22CE", "#B45309", "#0E7490", "#BE185D" ];

    // The palette is shared by headers, cell styles and selection/card buttons.
    // Freezing makes the cached brush safe to reuse without per-element
    // change notifications; color and per-control state remain unchanged.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Brush> StageBrushes =
        new(StringComparer.Ordinal);

    private static Brush StageBrush(string hex) => StageBrushes.GetOrAdd(hex, static key =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key));
        brush.Freeze();
        return brush;
    });

    private static string ShortStageName(string name)
    {
        var index = name.IndexOf(" — ", StringComparison.Ordinal);
        return index >= 0 ? name[(index + 3)..] : name;
    }

    private void RebuildInlineStageColumns()
    {
        var data = _inlineStagesData;
        if (data is null) return;
        var keys = data.Stages.Select(s => new StageColumnKey(s.Id, ShortStageName(s.Name), s.Locked)).ToArray();
        if (_inlineStageColumnKeys.SequenceEqual(keys) &&
            _inlineStageColumns.Count == keys.Length + 1 &&
            _inlineStageColumns.All(EstimateGrid.Columns.Contains))
            return;
        foreach (var col in _inlineStageColumns) EstimateGrid.Columns.Remove(col);
        _inlineStageColumns.Clear();
        _inlineStageIds.Clear();
        _hiddenInlineStageIds.RemoveWhere(id => id != 0 && !keys.Any(key => key.Id == id));
        _inlineStageColumnKeys = keys;
        for (var i = 0; i <= data.Stages.Count; i++)
        {
            var rest = i == data.Stages.Count;
            var id = rest ? 0L : data.Stages[i].Id;
            var title = rest ? "Остаток" : ShortStageName(data.Stages[i].Name);
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
            style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0)));
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty,
                StageBrush(rest ? "#334155" : StageInk[i % StageInk.Length])));
            if (rest)
            {
                var warning = new DataTrigger { Binding = new Binding(nameof(EditableEstimateRow.StageOverrun)),
                    Value = true };
                warning.Setters.Add(new Setter(TextBlock.ForegroundProperty, StageBrush("#DC2626")));
                style.Triggers.Add(warning);
            }
            // Selection wins over stage tint and overrun text; the focused
            // cell retains the common overlay without changing its geometry.
            var selected = new DataTrigger
            {
                Binding = new Binding(nameof(DataGridCell.IsSelected))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(DataGridCell), 1)
                },
                Value = true
            };
            selected.Setters.Add(new Setter(TextBlock.ForegroundProperty, FindResource("GridSelectionTextBrush")));
            style.Triggers.Add(selected);
            // DataGridBoundColumn coerces any OneWay binding to read-only,
            // even when IsReadOnly=false. A template column keeps the snapshot
            // immutable while the existing CellEditEnding reads the editor text.
            var display = new FrameworkElementFactory(typeof(TextBlock));
            display.SetValue(FrameworkElement.StyleProperty, style);
            display.SetBinding(TextBlock.TextProperty,
                new Binding($"StageDisplay[{i}]") { Mode = BindingMode.OneWay });
            var editor = new FrameworkElementFactory(typeof(TextBox));
            editor.SetValue(FrameworkElement.StyleProperty, (Style)FindResource("EstimateStageEditor"));
            editor.SetBinding(TextBox.TextProperty,
                new Binding($"StageDisplay[{i}]") { Mode = BindingMode.OneWay });
            var column = new DataGridTemplateColumn
            {
                Header = title,
                Visibility = Visibility.Visible,
                CellTemplate = new DataTemplate { VisualTree = display },
                CellEditingTemplate = new DataTemplate { VisualTree = editor },
                Width = new DataGridLength(Math.Clamp(90 + title.Length * 5, 90, 170)),
                IsReadOnly = rest || data.Stages[i].Locked,
                CanUserSort = false,
                CanUserReorder = false,
                HeaderStyle = CreateInlineStageHeaderStyle(i, rest),
                HeaderTemplate = CreateInlineStageHeaderTemplate()
            };
            _inlineStageColumns.Add(column);
            _inlineStageIds.Add(column, id);
            if (_hiddenInlineStageIds.Contains(id))
                column.Visibility = Visibility.Collapsed;
            EstimateGrid.Columns.Add(column);
            column.DisplayIndex = EstimateGrid.Columns.Count - 1;
        }
        RequestEstimateColumnFit();
    }

    private void RevealInlineStageColumns(InlineStagesSnapshot snapshot)
    {
        // Do not change selection, current cell or the vertical scroll position.
        // Bring the first stage just inside the right edge, even with no rows.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_inlineStagesOn || _detached || !ReferenceEquals(snapshot, _inlineStagesData) ||
                !EstimateGrid.IsVisible || _inlineStageColumns.Count == 0 ||
                EstimateScrollViewer() is not { } scroll || scroll.ViewportWidth <= 0) return;
            var first = _inlineStageColumns.FirstOrDefault(c => c.Visibility == Visibility.Visible);
            if (first is null) { _revealInlineStageColumns = false; return; }
            var right = EstimateGrid.Columns
                .Where(c => c.Visibility == Visibility.Visible && c.DisplayIndex <= first.DisplayIndex)
                .Sum(c => c.ActualWidth);
            if (right > scroll.HorizontalOffset + scroll.ViewportWidth)
                scroll.ScrollToHorizontalOffset(Math.Max(0, right - scroll.ViewportWidth + 4));
            _revealInlineStageColumns = false;
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private void DrawInlineStageCards()
    {
        InlineStagesCards.Children.Clear();
        var data = _inlineStagesData;
        if (data is null) return;
        if (data.Stages.Count == 0)
        {
            InlineStagesCards.Children.Add(new TextBlock
            {
                Text = "Этапов пока нет — нажмите «+ Этап»",
                Foreground = StageBrush("#64748B"), Margin = new Thickness(3)
            });
        }
        var metrics = data.Metrics;
        for (var i = 0; i < data.Stages.Count; i++)
        {
            var stage = data.Stages[i];
            var total = data.DocumentTotals.GetValueOrDefault(stage.Id);
            var count = metrics.AssignedCounts[i];
            var hasDifference = stage.StageSum is decimal goal && Math.Abs(total - goal) >= 1m;
            var status = stage.StageSum is null ? (stage.Locked ? "Подписан" : "В работе") :
                hasDifference ? $"Разница {N2(total - stage.StageSum.Value)} ₽" :
                stage.Locked ? "Подписан · сумма совпадает" : "Сумма совпадает";
            AddCard(stage.Id, ShortStageName(stage.Name), $"{N2(total)} ₽",
                $"{count} поз. · {status}", StageTints[i % StageTints.Length],
                StageInk[i % StageInk.Length], hasDifference);
        }
        if (data.Rest is { } rest)
        {
            var total = data.DocumentTotals.GetValueOrDefault(rest.Id);
            AddCard(rest.Id, "Остаток", $"{N2(total)} ₽",
                "Рассчитывается автоматически", "#F1F4F9", "#64748B", false);
        }

        InlineStagesSummary.Text =
            $"Не распределено строк: {metrics.UnassignedCount} · на {N2(metrics.RemainingAmount)} ₽" +
            (metrics.OverrunCount > 0 ? $" · Перебор: {metrics.OverrunCount}" : "");
    }

    private void InlineStageHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGridColumnHeader header || _inlineStagesData is not { } data) return;
        var index = _inlineStageColumns.IndexOf(header.Column);
        if (index < 0 || index >= data.Stages.Count) return;
        e.Handled = true;
        ShowInlineStageMenu(header, data.Stages[index], data);
    }

    private void ShowInlineStageMenu(FrameworkElement anchor, SqlServerStages.StageInfo stage,
        InlineStagesSnapshot snapshot)
    {
        if (_detached || _inlineStagesLoading || !ReferenceEquals(snapshot, _inlineStagesData)) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        var name = stage.Rest ? "Остаток" : ShortStageName(stage.Name);
        bool Writable() => BulkStageAllowed && ReferenceEquals(snapshot, _inlineStagesData) &&
            !stage.Rest && !stage.Locked && stage.StageSum is null;
        var open = ContextMenuIcons.Create($"Открыть КП «{name}»", "File");
        open.Click += (_, _) =>
        {
            if (ReferenceEquals(snapshot, _inlineStagesData) && Window.GetWindow(this) is MainWindow main)
                main.NavigateToStageEstimate(checked((int)stage.Id), _objectId);
        };
        menu.Items.Add(open);
        void Add(string label, string glyph, string action, bool danger = false)
        {
            var item = ContextMenuIcons.Create(label, glyph, Writable(), danger: danger);
            item.Click += async (_, _) =>
            {
                if (!Writable()) return;
                await InvokeInlineStageActionAsync(stage.Id, action, snapshot);
            };
            menu.Items.Add(item);
        }
        if (stage.Rest)
        {
            menu.Items.Add(new Separator());
            var info = new MenuItem { Header = "Остаток рассчитывается автоматически", IsEnabled = false };
            menu.Items.Add(info);
        }
        else
        {
            Add("СМР по системам…", "List", "systems");
            Add("Название…", "Edit", "rename");
            Add("СМР, транспорт и сумма по договору…", "List", "money");
            menu.Items.Add(new Separator());
            Add($"Весь остаток — в «{name}»", "Download", "put");
            Add($"Очистить «{name}»", "X", "clear", danger: true);
            Add($"Удалить «{name}»…", "Trash", "delete", danger: true);
        }
        menu.IsOpen = true;
    }

    /// <summary>True only for individual rows of an editable, unsigned stage.</summary>
    private bool CanEditInlineStage(EditableEstimateRow row, DataGridColumn column,
        out int stageIndex)
    {
        stageIndex = _inlineStageColumns.IndexOf(column);
        var data = _inlineStagesData;
        return !_detached && _inlineStagesOn && !_inlineStagesLoading && !_inlineStageCommandBusy && data is not null &&
            !string.IsNullOrWhiteSpace(data.Revision) && data.Revision == _serverRevision &&
            stageIndex >= 0 && stageIndex < data.Stages.Count &&
            !data.Stages[stageIndex].Locked &&
            !row.IsSystem && !row.IsSub && !row.IsVirtual &&
            data.Rows.ContainsKey(row.Id) && !_serverConflict &&
            System.Threading.Volatile.Read(ref _pendingWrites) == 0;
    }

    /// <summary>
    /// Python StageMode.save: validate source row, require confirmation
    /// for exceeding the available remainder, then apply the stage/rest
    /// changes inside a SINGLE serializable SQL transaction.
    /// </summary>
    private async void SaveInlineStageCell(
        EditableEstimateRow row, DataGridColumn column, string input)
    {
        if (!CanEditInlineStage(row, column, out var index) ||
            _inlineStagesData is not { } snapshot ||
            !snapshot.Rows.TryGetValue(row.Id, out var source))
            return;

        if (!StageAllocationPresentation.TryParseNonnegative(input, out var qty))
        {
            SetStatus("Некорректное количество этапа. Введи число или формулу.", true);
            return;
        }
        var stage = snapshot.Stages[index];
        var old = source.PerStage[index];
        var formula = NumberText.FormulaText(input);
        if (formula is not null && decimal.Round(qty, 6) <= StageAllocationPresentation.Epsilon)
        {
            SetStatus("Нулевое количество удаляет строку этапа. Для удаления введите 0 без формулы.", true);
            return;
        }
        var previousFormula = source.StageFormulas.ElementAtOrDefault(index) ?? "";
        if (Math.Abs(old - qty) <= StageAllocationPresentation.Epsilon && previousFormula == (formula ?? "")) return;

        if (StageAllocationPresentation.NeedsOverrunConfirmation(
            source.Qty, source.PerStage, index, qty))
        {
            var available = source.Qty -
                source.PerStage.Where((_, i) => i != index).Sum();
            var availableText = Math.Max(0m, available).ToString(
                "0.######", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            var response = MessageBox.Show(Window.GetWindow(this),
                $"Осталось {availableText} " +
                $"{source.Unit}. Записать {StageAllocationPresentation.Qty(qty)} " +
                $"в «{ShortStageName(stage.Name)}» — больше остатка?",
                "Больше остатка", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (response != MessageBoxResult.Yes)
            {
                SetStatus("Изменение этапа отменено.", false);
                return;
            }
        }

        // The confirmation belongs to the original displayed allocation.
        if (!ReferenceEquals(snapshot, _inlineStagesData) ||
            !CanEditInlineStage(row, column, out var currentIndex) || currentIndex != index)
        {
            SetStatus("Распределение изменилось. Повтори ввод после обновления.", true);
            return;
        }

        var revision = snapshot.Revision;
        _reloadTimer.Stop();
        AttachSaveCloseGuard();
        System.Threading.Interlocked.Increment(ref _pendingWrites);
        SetStatus($"Этап «{ShortStageName(stage.Name)}»: сохранение…", false);
        await _writeLock.WaitAsync();
        try
        {
            if (_serverConflict || _detached || revision != _serverRevision ||
                !ReferenceEquals(snapshot, _inlineStagesData))
                throw new InvalidOperationException("Распределение изменилось до записи.");
            var result = await Task.Run(() => SqlServerStages.SetStageQty(
                _sqlConnectionString, stage.Id, source.SrcId, qty,
                revision, old, source.Rest, formula));
            if (!result.Saved)
            {
                _saveError = "Этап не сохранён: " + result.Error;
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
            _saveError = "Запись этапа не подтверждена: " + ex.Message;
        }
        finally
        {
            _writeLock.Release();
            if (System.Threading.Interlocked.Decrement(ref _pendingWrites) == 0)
            {
                SetStatus(_saveError ?? "Этап записан, выполняется сверка…", _saveError is not null);
                _reloadTimer.Stop();
                _reloadTimer.Start();
            }
        }
    }

    private void InlineStagesFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_inlineStagesOn || _inlineStagesData is null ||
            InlineStagesFilter.SelectedItem is not ComboBoxItem item)
            return;
        var filter = item.Tag as string ?? StageAllocationPresentation.All;
        if (_inlineStagesFilter == filter) return;
        _inlineStagesFilter = filter;
        ApplyFilter();
    }

    /// <summary>Computes the stage read-only cells for original or equal-group rows.</summary>
    private void FillInlineStageCells(EditableEstimateRow row)
    {
        var data = _inlineStagesData;
        if (!_inlineStagesOn || data is null || row.HasStagePresentation(_inlineStagesVersion)) return;
        if (row.IsSystem || row.IsSub || row.IsTypeHeader)
        {
            row.PublishStagePresentation(_inlineStagesVersion, _blankStageLabels, false);
            return;
        }

        // Real positions already contain all the authoritative allocation
        // values. Only synthetic equal-position groups need aggregation.
        if (!row.IsGroup)
        {
            if (data.Rows.TryGetValue(row.Id, out var source))
                row.PublishStagePresentation(_inlineStagesVersion,
                    StageAllocationPresentation.CellLabels(source.PerStage, source.Rest),
                    source.Rest < -StageAllocationPresentation.Epsilon);
            else
                row.PublishStagePresentation(_inlineStagesVersion, _blankStageLabels, false);
            return;
        }

        var values = new decimal[data.Stages.Count];
        decimal remaining = 0;
        var hasMember = false;
        foreach (var id in row.GroupMemberIds)
        {
            if (!data.Rows.TryGetValue(checked((int)id), out var found)) continue;
            hasMember = true;
            for (var i = 0; i < values.Length; i++) values[i] += found.PerStage[i];
            remaining += found.Rest;
        }
        row.PublishStagePresentation(_inlineStagesVersion,
            hasMember ? StageAllocationPresentation.CellLabels(values, remaining) : _blankStageLabels,
            hasMember && remaining < -StageAllocationPresentation.Epsilon);
    }

    /// <summary>
    /// Search, "Вид" and stage filter compose: keep matching real rows
    /// and their tree headers without changing SQL position order.
    /// </summary>
    private List<EditableEstimateRow> ApplyInlineStagesFilter(List<EditableEstimateRow> rows)
    {
        if (!_inlineStagesOn || _inlineStagesData is null) return rows;
        if (_inlineStagesFilter == StageAllocationPresentation.All)
        {
            foreach (var row in rows) FillInlineStageCells(row);
            return rows;
        }

        var keep = StageFilterAncestors.Mark(rows.Count,
            i => rows[i].DisplayIndentLevel,
            i => rows[i].IsSystem || rows[i].IsGroup || rows[i].IsTypeHeader,
            i =>
            {
                var row = rows[i];
                if (row.IsSystem || row.IsSub || row.IsTypeHeader) return false;
                bool Matches(long id) => _inlineStagesData.Rows.TryGetValue(checked((int)id),
                    out var source) && StageAllocationPresentation.Matches(
                        source.Rest, source.PerStage, _inlineStagesFilter);
                return row.IsGroup ? row.GroupMemberIds.Any(Matches) : Matches(row.Id);
            });
        var visible = new List<EditableEstimateRow>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (!keep[i]) continue;
            FillInlineStageCells(rows[i]);
            visible.Add(rows[i]);
        }
        return visible;
    }

}
