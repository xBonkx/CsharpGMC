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
        Dictionary<long, decimal> DocumentTotals, string Revision);

    private bool _inlineStagesOn;
    private bool _requestInlineStages;
    private bool _inlineStageCommandBusy;
    private bool _inlineStagesLoading;
    private int _inlineStagesVersion;
    private InlineStagesSnapshot? _inlineStagesData;
    private string _inlineStagesFilter = StageAllocationPresentation.All;
    private readonly List<DataGridColumn> _inlineStageColumns = new();
    private readonly record struct StageColumnKey(long Id, string Title, bool Locked);
    private StageColumnKey[] _inlineStageColumnKeys = Array.Empty<StageColumnKey>();
    private IReadOnlyList<string> _blankStageLabels = Array.Empty<string>();

    private async void EstimateView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isKp && !_inlineStagesOn &&
            (_uiSettings.StageModeKp ?? new List<int>()).Contains(_contractId))
            await EnableInlineStagesAsync();
    }

    private async void ToggleInlineStages_Click(object sender, RoutedEventArgs e)
    {
        if (_inlineStageCommandBusy) return;
        if (_inlineStagesOn) { DisableInlineStages(); return; }
        await EnableInlineStagesAsync();
    }

    internal void RequestInlineStages()
    {
        if (!_isKp || _detached) return;
        _requestInlineStages = true;
        if (_estimate is not null) ApplyRequestedInlineStages();
    }

    private void ApplyRequestedInlineStages()
    {
        if (!_requestInlineStages || _estimate is null) return;
        _requestInlineStages = false;
        EstimateModeTab.IsChecked = true;
        if (!_inlineStagesOn) _ = EnableInlineStagesAsync();
    }

    private async Task EnableInlineStagesAsync()
    {
        if (!_isKp || _inlineStagesOn) return;
        _inlineStagesOn = true;
        InlineStagesBar.Visibility = Visibility.Visible;
        var loaded = await RefreshInlineStagesAsync();
        if (!loaded)
        {
            DisableInlineStages();
            return;
        }
        var stored = _uiSettings.StageModeKp ?? new List<int>();
        if (!stored.Contains(_contractId)) stored.Add(_contractId);
        _uiSettings.StageModeKp = stored;
        SaveEstimateUiSettings();
    }

    private void CloseInlineStages_Click(object sender, RoutedEventArgs e)
    {
        if (!_inlineStageCommandBusy) DisableInlineStages();
    }

    private void DisableInlineStages()
    {
        _inlineStagesOn = false;
        _inlineStagesVersion++; // discard any read finishing after exit
        _inlineStagesData = null;
        _inlineStagesFilter = StageAllocationPresentation.All;
        if (InlineStagesFilter is not null) InlineStagesFilter.SelectedIndex = 0;
        if (InlineStagesBar is not null) InlineStagesBar.Visibility = Visibility.Collapsed;
        foreach (var column in _inlineStageColumns)
            EstimateGrid.Columns.Remove(column);
        _inlineStageColumns.Clear();
        _inlineStageColumnKeys = Array.Empty<StageColumnKey>();
        _blankStageLabels = Array.Empty<string>();
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
        if (!_inlineStagesOn) return false;
        var version = ++_inlineStagesVersion;
        _inlineStagesLoading = true;
        _inlineStagesData = null; // No stale allocation may remain writable after a failed read.
        RefreshInlineStageCommandButtons();
        InlineStagesSummary.Text = "Читаю распределение из SQL Server…";
        try
        {
            var data = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_sqlConnectionString);
                var revisionBefore = SqlServerDatabase.Revision(conn);
                var full = SqlServerStages.FullOf(conn, _contractId);
                if (full != _contractId)
                    throw new InvalidOperationException("Это КП этапа. Режим распределения открывается в полном КП.");
                var (stages, rows, rest) = SqlServerStages.ReadStageDistribution(conn, full);

                // Totals.ReadOne scans all contracts and positions. Running it
                // for each stage caused N full-DB scans on every cell edit.
                // Calculate once and select only this full KP's stages.
                var allTotals = Totals.ReadAll(conn)
                    .ToDictionary(t => (long)t.Id, t => t.Total);
                var totals = new Dictionary<long, decimal>();
                foreach (var stage in stages)
                    totals[stage.Id] = allTotals.GetValueOrDefault(stage.Id);
                if (rest is not null)
                    totals[rest.Id] = allTotals.GetValueOrDefault(rest.Id);

                if (string.IsNullOrWhiteSpace(revisionBefore) || revisionBefore != SqlServerDatabase.Revision(conn))
                    throw new InvalidOperationException(
                        "Распределение изменилось во время чтения. Обнови смету.");
                return new InlineStagesSnapshot(stages,
                    rows.ToDictionary(r => checked((int)r.SrcId)),
                    rest, totals, revisionBefore);
            });
            if (!_inlineStagesOn || _detached || version != _inlineStagesVersion) return false;
            _inlineStagesData = data;
            _blankStageLabels = Array.AsReadOnly(
                Enumerable.Repeat("", data.Stages.Count + 1).ToArray());
            RebuildInlineStageColumns();
            DrawInlineStageCards();
            ApplyFilter();
            return true;
        }
        catch (Exception ex)
        {
            if (_inlineStagesOn && version == _inlineStagesVersion)
            {
                SetStatus("Этапы: " + ex.Message, true);
                InlineStagesSummary.Text = "Ошибка чтения этапов: " + ex.Message;
            }
            return false;
        }
        finally
        {
            if (version == _inlineStagesVersion)
            {
                _inlineStagesLoading = false;
                RefreshInlineStagesSelectionButtons();
            }
        }
    }

    private static readonly string[] StageTints =
    [ "#E8F0FF", "#EAF7EE", "#F4ECFF", "#FFF4E0", "#E6F6F8", "#FBEAF0" ];
    private static readonly string[] StageInk =
    [ "#1D4ED8", "#15803D", "#7E22CE", "#B45309", "#0E7490", "#BE185D" ];

    private static Brush StageBrush(string hex) =>
        new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

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
        _inlineStageColumnKeys = keys;
        for (var i = 0; i <= data.Stages.Count; i++)
        {
            var rest = i == data.Stages.Count;
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
            var column = new DataGridTextColumn
            {
                Header = title,
                Binding = new Binding($"StageDisplay[{i}]") { Mode = BindingMode.OneWay },
                ElementStyle = style,
                EditingElementStyle = (Style)FindResource("EstimateMoneyEditor"),
                Width = new DataGridLength(Math.Clamp(90 + title.Length * 5, 90, 170)),
                IsReadOnly = rest || data.Stages[i].Locked,
                CanUserSort = false,
                CanUserReorder = false,
                HeaderStyle = new Style(typeof(DataGridColumnHeader))
                {
                    Setters =
                    {
                        new Setter(Control.BackgroundProperty, StageBrush(rest
                            ? "#F1F4F9" : StageTints[i % StageTints.Length])),
                        new Setter(Control.ForegroundProperty, StageBrush(rest
                            ? "#334155" : StageInk[i % StageInk.Length])),
                        new Setter(Control.FontWeightProperty, FontWeights.Bold)
                    }
                }
            };
            column.HeaderStyle.Setters.Add(new EventSetter(ButtonBase.ClickEvent,
                new RoutedEventHandler(InlineStageHeader_Click)));
            _inlineStageColumns.Add(column);
            EstimateGrid.Columns.Add(column);
        }
        RequestEstimateColumnFit();
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
        var metrics = StageAllocationPresentation.Summarize(data.Stages.Count,
            data.Rows.Values.Select(r => (r.Price, r.Rest, (IReadOnlyList<decimal>)r.PerStage)));
        for (var i = 0; i < data.Stages.Count; i++)
        {
            var stage = data.Stages[i];
            var total = data.DocumentTotals.GetValueOrDefault(stage.Id);
            var count = metrics.AssignedCounts[i];
            var hasDifference = stage.StageSum is decimal goal && Math.Abs(total - goal) >= 1m;
            var second = $"{N2(total)} ₽ · строк {count}" +
                (stage.StageSum is null ? "" :
                    hasDifference ? $" · разница {N2(total - stage.StageSum.Value)} ₽"
                                  : " · ✓ как в договоре");
            AddCard(stage.Id, ShortStageName(stage.Name),
                second + (stage.Locked ? " · подписан" : ""),
                StageTints[i % StageTints.Length], hasDifference);
        }
        if (data.Rest is { } rest)
        {
            var total = data.DocumentTotals.GetValueOrDefault(rest.Id);
            AddCard(rest.Id, "Остаток (считается сам)",
                $"{N2(total)} ₽", "#F8FAFC", false);
        }

        InlineStagesSummary.Text =
            $"Не распределено строк: {metrics.UnassignedCount} · на {N2(metrics.RemainingAmount)} ₽" +
            (metrics.OverrunCount > 0 ? $" · Перебор: {metrics.OverrunCount}" : "");
    }

    private void AddCard(long id, string name, string secondLine, string tint, bool warning)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = name, FontWeight = FontWeights.Bold, Foreground = StageBrush("#1E293B")
        });
        body.Children.Add(new TextBlock
        {
            Text = secondLine, Foreground = StageBrush(warning ? "#DC2626" : "#64748B"),
            TextWrapping = TextWrapping.Wrap, FontSize = 11
        });
        var button = new Button
        {
            Content = body, Background = StageBrush(tint),
            BorderBrush = StageBrush(warning ? "#DC2626" : "#E4E8EF"),
            BorderThickness = new Thickness(1), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 7, 5), MinWidth = 132,
            ToolTip = "Меню этапа: открыть КП, название, суммы, распределение"
        };
        var snapshot = _inlineStagesData;
        button.Click += (_, _) =>
        {
            if (snapshot is null || !ReferenceEquals(snapshot, _inlineStagesData)) return;
            var stage = snapshot.Stages.FirstOrDefault(item => item.Id == id) ??
                (snapshot.Rest?.Id == id ? snapshot.Rest : null);
            if (stage is not null) ShowInlineStageMenu(button, stage, snapshot);
        };
        InlineStagesCards.Children.Add(button);
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
            !stage.Locked && stage.StageSum is null;
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
            Add("СМР по системам…", "List", "systems");
            Add("СМР и транспорт…", "List", "money");
            menu.Items.Add(new Separator());
            Add("Удалить документ «Остаток»…", "Trash", "delete", danger: true);
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
        if (Math.Abs(old - qty) <= StageAllocationPresentation.Epsilon) return;

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
                revision, old, source.Rest));
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
