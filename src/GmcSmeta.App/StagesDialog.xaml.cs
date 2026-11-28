using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.ComponentModel;
using GmcSmeta.Core.Domain;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// «Этапы» (stages_dialog.py): разбивка КП на 100 % работы по этапам. Таблица —
/// каждая строка полного КП · Всего · Этап 1 · … · Остаток. Двойной щелчок по
/// ячейке этапа — количество; контекстное меню заголовка этапа — команды.
/// Каждый этап — обычное КП объекта: цены, скидки, Excel, договор.
/// </summary>
public partial class StagesDialog : Window
{
    private readonly string _connectionString;
    private readonly int _fullId;
    private readonly string _fullTitle;
    private readonly Dictionary<int, long> _columnStages = new();   // индекс колонки → id этапа
    private bool _building;
    private bool _quantityWriteBusy;
    private string? _fullMutationProblem = "Нет подтверждённого снимка.";
    private bool FullEditable => _fullMutationProblem is null;
    private SqlServerStages.StageInfo? _restDocument;
    private List<SqlServerStages.StageInfo> _cachedStages = new();
    private DataTable? _table;
    private bool _closed;
    private CancellationTokenSource? _distributionCancellation;
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    internal event Action? InitialSnapshotReady;

    public StagesDialog(string connectionString, int fullId, string fullTitle)
    {
        _connectionString = connectionString;
        _fullId = fullId;
        _fullTitle = fullTitle;
        InitializeComponent();
        CaptionText.Text = $"Этапы: {fullTitle}";
        _searchTimer.Tick += SearchTimer_Tick;
        Loaded += InitialLoadAsync;
        SetReadControls(false);
        SetStatus("Загрузка этапов…");
    }

    private async void InitialLoadAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= InitialLoadAsync;
        try
        {
            await ReloadAsync();
            if (!_closed && !string.IsNullOrWhiteSpace(_revision))
            {
                SetStatus("");
                InitialSnapshotReady?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if (!_closed) SetStatus("Не удалось загрузить этапы: " + ex.Message);
        }
    }

    private void SetReadControls(bool enabled)
    {
        StagesGrid.IsEnabled = SearchBox.IsEnabled = enabled;
        AddStageButton.IsEnabled = CreateRestButton.IsEnabled = enabled && FullEditable;
        AddStageButton.ToolTip = CreateRestButton.ToolTip = _fullMutationProblem;
    }

    private async Task ReloadAsync()
    {
        if (_closed) return;
        using var cancellation = new CancellationTokenSource();
        _distributionCancellation = cancellation;
        var token = cancellation.Token;
        _revision = "";
        _fullMutationProblem = "Нет подтверждённого снимка.";
        _restDocument = null;
        _searchTimer.Stop();
        _building = true;
        SetReadControls(false);
        try
        {
            var data = await Task.Run(async () =>
            {
                using var conn = await SqlServerDatabase.OpenAsync(_connectionString, token).ConfigureAwait(false);
                var revision = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                var (stages, rows, rest) = SqlServerStages.ReadStageDistribution(conn, _fullId, token);
                var problem = SqlServerStages.ReadFullStageMutationProblem(conn, _fullId, token);
                if (string.IsNullOrWhiteSpace(revision) ||
                    revision != await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false))
                    throw new InvalidOperationException("Данные изменились при загрузке этапов. Откройте окно снова.");
                return (Stages: stages, Remaining: rest, Revision: revision, Table: BuildTable(stages, rows, token), Problem: problem);
            }, token);
            if (_closed || token.IsCancellationRequested) return;
            _fullMutationProblem = data.Problem;
            PublishSnapshot(data.Stages, data.Remaining, data.Table, data.Revision);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_distributionCancellation, cancellation)) _distributionCancellation = null;
            _building = false;
            if (!_closed) SetReadControls(!string.IsNullOrWhiteSpace(_revision));
        }
    }

    private void PublishSnapshot(List<SqlServerStages.StageInfo> stages,
        SqlServerStages.StageInfo? rest, DataTable table, string revision)
    {
        _columnStages.Clear();
        StagesGrid.Columns.Clear();
        StagesGrid.Columns.Add(MakeColumn("Раздел", "SystemName", 150, readOnly: true));
        StagesGrid.Columns.Add(MakeColumn("Наименование", "Name", 330, readOnly: true));
        StagesGrid.Columns.Add(MakeColumn("Ед.", "Unit", 46, readOnly: true));
        StagesGrid.Columns.Add(MakeColumn("Всего", "Qty", 74, readOnly: true));

        var stageIndex = 0;
        foreach (var stage in stages)
        {
            var shortName = ShortStageName(stage.Name);
            var column = MakeColumn(shortName, $"S{stageIndex}", 92,
                readOnly: !FullEditable || stage.Locked || stage.StageSum is not null);
            TableColumnMenu.SetStableId(column, "stage-" + stage.Id.ToString(CultureInfo.InvariantCulture));
            StagesGrid.Columns.Add(column);
            _columnStages[StagesGrid.Columns.IndexOf(column)] = stage.Id;
            stageIndex++;
        }
        var restColumn = MakeColumn("Остаток", "Rest", 92, readOnly: true);
        StagesGrid.Columns.Add(restColumn);

        _cachedStages = stages;
        _restDocument = rest;
        _table = table;
        table.DefaultView.RowFilter = StageTableSearch.Expression(SearchBox.Text);
        StagesGrid.ItemsSource = table.DefaultView;
        TableColumnMenu.SetProtect(StagesGrid, "Раздел|Наименование|Всего|Остаток");
        TableColumnMenu.RefreshDynamic(StagesGrid, "stage-distribution-" + _fullId.ToString(CultureInfo.InvariantCulture), ownHeaderMenu: false);
        _revision = revision;
    }

    // Build the detached table on the worker; publish once on the dispatcher.
    private static DataTable BuildTable(IReadOnlyList<SqlServerStages.StageInfo> stages,
        IReadOnlyList<SqlServerStages.StageTableRow> rows, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var table = new DataTable { Locale = CultureInfo.InvariantCulture, CaseSensitive = true };
        table.Columns.Add("SrcId", typeof(long));
        table.Columns.Add("SystemName", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Unit", typeof(string));
        table.Columns.Add("Qty", typeof(string));
        table.Columns.Add("__QtyExact", typeof(decimal));
        var displayColumns = new DataColumn[stages.Count];
        var exactColumns = new DataColumn[stages.Count];
        for (var i = 0; i < stages.Count; i++)
        {
            displayColumns[i] = table.Columns.Add($"S{i}", typeof(string));
            exactColumns[i] = table.Columns.Add($"__S{i}", typeof(decimal));
        }
        table.Columns.Add("Rest", typeof(string));
        table.Columns.Add("__RestExact", typeof(decimal));
        table.Columns.Add(StageTableSearch.Column, typeof(string));
        table.BeginLoadData();
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            var record = table.NewRow();
            record[StageTableSearch.Column] = $"{row.SystemName} · {row.Name} · {row.Unit}".ToLowerInvariant();
            record["SrcId"] = row.SrcId;
            record["SystemName"] = row.SystemName;
            record["Name"] = row.Name;
            record["Unit"] = row.Unit;
            record["Qty"] = QtyText(row.Qty);
            record["__QtyExact"] = row.Qty;
            for (var i = 0; i < stages.Count; i++)
            {
                var quantity = row.PerStage[i];
                record[displayColumns[i]] = quantity == 0m ? "" : QtyText(quantity);
                record[exactColumns[i]] = quantity;
            }
            record["Rest"] = row.Rest == 0m ? "" : QtyText(row.Rest);
            record["__RestExact"] = row.Rest;
            table.Rows.Add(record);
        }
        token.ThrowIfCancellationRequested();
        table.EndLoadData();
        token.ThrowIfCancellationRequested();
        return table;
    }

    private static string QtyText(decimal v) => v == decimal.Truncate(v)
        ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.###", CultureInfo.CurrentCulture);

    private static string ShortStageName(string name)
    {
        // «База — Этап 2» / «База — Остаток» → «Этап 2» / «Остаток»
        var dash = name.IndexOf(" — ", StringComparison.Ordinal);
        return dash >= 0 ? name[(dash + 3)..] : name;
    }

    private DataGridTextColumn MakeColumn(string header, string binding, double width, bool readOnly)
    {
        // stages_dialog.py: names left, unit centered, stage quantities right.
        var text = binding is "SystemName" or "Name";
        var unit = binding == "Unit";
        var column = new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(binding) { Mode = readOnly
                ? System.Windows.Data.BindingMode.OneWay : System.Windows.Data.BindingMode.TwoWay },
            Width = width,
            IsReadOnly = readOnly,
            ElementStyle = (Style)FindResource(text ? "CompactCostText" : unit ? "CompactCostCenter" : "CompactCostMoney"),
            HeaderStyle = (Style)FindResource(text ? "KsTextHeader" : unit ? "KsQuantityHeader" : "KsMoneyHeader"),
            EditingElementStyle = (Style)FindResource(text ? "CompactCostEditor" : unit ? "CompactCostCenterEditor" : "CompactCostMoneyEditor"),
        };
        if (!text) TableColumnMenu.SetFitDisplayPath(column, binding);
        return column;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => QueueSearch();

    private void QueueSearch()
    {
        if (_closed || _building || _quantityWriteBusy || _table is null) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchTimer_Tick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        if (_closed || _building || _quantityWriteBusy || _table is null ||
            System.Windows.Data.CollectionViewSource.GetDefaultView(StagesGrid.ItemsSource)
                is IEditableCollectionView { IsEditingItem: true }) return;
        try
        {
            var expression = StageTableSearch.Expression(SearchBox.Text);
            if (_table.DefaultView.RowFilter != expression)
                _table.DefaultView.RowFilter = expression;
        }
        catch (InvalidExpressionException ex)
        {
            SetStatus("Не удалось применить поиск: " + ex.Message);
        }
    }

    private void StagesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e) => QueueSearch();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_quantityWriteBusy) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _distributionCancellation?.Cancel();
        _searchTimer.Stop();
        _searchTimer.Tick -= SearchTimer_Tick;
        InitialSnapshotReady = null;
        base.OnClosed(e);
    }

    private void EndWrite()
    {
        _quantityWriteBusy = false;
        QueueSearch();
    }

    private void StagesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!FullEditable || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision) ||
            !_columnStages.ContainsKey(StagesGrid.Columns.IndexOf(e.Column)))
            e.Cancel = true;
    }

    private async void StagesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _building) return;
        if (!FullEditable || string.IsNullOrWhiteSpace(_revision)) { e.Cancel = true; return; }
        if (_quantityWriteBusy) { e.Cancel = true; return; }
        if (!_columnStages.TryGetValue(StagesGrid.Columns.IndexOf(e.Column), out var stageId)) return;
        if (e.Row.Item is not DataRowView row) return;
        var srcId = Convert.ToInt64(row.Row["SrcId"]);
        var revision = ServerRevision();
        var table = _table;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (table is null || !ReferenceEquals(row.Row.Table, table) || stage is null ||
            stage.Locked || stage.Rest || stage.StageSum is not null)
        { e.Cancel = true; return; }
        var snapshotRest = (decimal)row.Row["__RestExact"];
        var editor = e.EditingElement as TextBox;
        var input = editor?.Text.Trim() ?? "";
        // A mistyped stage quantity must never silently become zero and
        // delete the stage's row. Same safe parser as the Python KP editor.
        if (!GmcSmeta.Core.Domain.StageAllocationPresentation.TryParseNonnegative(input, out var value))
        {
            e.Cancel = true;
            SetStatus("Некорректное количество. Исправь значение или нажми Esc.");
            return;
        }
        // Python stage_mode.save: exceeding the remaining full-KP quantity
        // is allowed only after explicit confirmation. Do not silently overfill.
        var columnIndex = StagesGrid.Columns.IndexOf(e.Column) - 4;
        // Use hidden full-precision values. The displayed QtyText rounds to
        // three decimal places and is not valid for SQL snapshot comparison.
        var originalQty = (decimal)row.Row["__QtyExact"];
        var allocations = Enumerable.Range(0, _columnStages.Count)
            .Select(i => (decimal)row.Row[$"__S{i}"])
            .ToArray();
        if (columnIndex >= 0 && columnIndex < allocations.Length &&
            GmcSmeta.Core.Domain.StageAllocationPresentation.NeedsOverrunConfirmation(
                originalQty, allocations, columnIndex, value))
        {
            var available = originalQty - allocations
                .Where((_, index) => index != columnIndex).Sum();
            if (MessageBox.Show(this,
                $"В полном КП доступно {Math.Max(0m, available):0.######} " +
                $"{row.Row["Unit"]}. Записать {value:0.######} в этап — больше остатка?",
                "Больше остатка", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                SetStatus("Изменение количества отменено.");
                return;
            }
        }
        if (!StageCommandCurrent(stage, revision) || !ReferenceEquals(table, _table) ||
            !ReferenceEquals(row.Row.Table, _table) ||
            !_columnStages.TryGetValue(StagesGrid.Columns.IndexOf(e.Column), out var currentStageId) ||
            currentStageId != stageId)
        {
            e.Cancel = true;
            SetStatus("Этап или снимок изменились. Повторите ввод.");
            return;
        }
        _quantityWriteBusy = true;
        SetStatus("Сохранение…");
        try
        {
            var snapshotStageQty = columnIndex >= 0 && columnIndex < allocations.Length
                ? allocations[columnIndex] : (decimal?)null;
            var result = await Task.Run(() => SqlServerStages.SetStageQty(
                _connectionString, stageId, srcId, value, revision,
                snapshotStageQty, snapshotRest));
            if (!result.Saved) SetStatus("Ошибка: " + result.Error);
            else
            {
                _revision = result.Revision;
                SetStatus("Количество сохранено. Вернуть — Ctrl+Z в смете этапа");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        try { await ReloadAsync(); }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Не удалось обновить этапы: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private void StagesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        StageMenu.Items.Clear();
        // Меню строк и заголовка принципиально разные: операция над всеми
        // строками этапа не должна быть доступна из ПКМ по обычной ячейке.
        var keyboard = e.CursorLeft < 0 && e.CursorTop < 0;
        var header = false;
        var target = keyboard ? StagesGrid.CurrentColumn : ColumnUnderMouse(out header);
        if (_building || _quantityWriteBusy)
        {
            e.Handled = true;
            return;
        }
        if (string.IsNullOrWhiteSpace(_revision))
        {
            // A missing SQL revision must block every data command, but not
            // the local column layout. No stale stage ID may reach a mutation.
            if (!keyboard && header)
                TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu);
            e.Handled = StageMenu.Items.Count == 0;
            return;
        }
        if (keyboard || !header)
        {
            var hit = ContextMenuTarget.SelectRow<DataRowView>(StagesGrid, e);
            if (hit is null)
            {
                e.Handled = true;
                return;
            }
            BuildStageRowsMenu();
            e.Handled = StageMenu.Items.Count == 0;
            return;
        }
        if (target is DataGridTextColumn { Binding: System.Windows.Data.Binding { Path.Path: "Rest" } })
        {
            var revision = _revision;
            var rest = _restDocument;
            var item = ContextMenuIcons.Create(rest is null ? "Создать документ «Остаток»" : "Открыть КП «Остаток»", rest is null ? "FilePlus" : "File", rest is not null || FullEditable);
            item.Click += (_, _) =>
            {
                if (_closed || _building || _quantityWriteBusy || revision != _revision ||
                    !ReferenceEquals(rest, _restDocument)) return;
                if (rest is null) CreateRest_Click(this, new RoutedEventArgs());
                else OpenStage(rest.Id);
            };
            StageMenu.Items.Add(item);
            // StageMutationPolicy explicitly forbids editing or deleting computed rest.
            TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu);
            return;
        }
        if (target is null || !_columnStages.TryGetValue(StagesGrid.Columns.IndexOf(target), out var stageId))
        {
            TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu);
            e.Handled = StageMenu.Items.Count == 0;
            return;
        }
        var stageName = (target.Header as string) ?? "этап";
        var selectedStage = _cachedStages.FirstOrDefault(stage => stage.Id == stageId && !stage.Rest);
        if (selectedStage is null)
        {
            TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu);
            e.Handled = StageMenu.Items.Count == 0;
            return;
        }
        var editableStage = FullEditable && !selectedStage.Locked && selectedStage.StageSum is null;
        var menuRevision = _revision;
        MenuItem Add(string header, string glyph, RoutedEventHandler onClick, bool enabled = true, bool danger = false)
        {
            var item = ContextMenuIcons.Create(header, glyph, enabled, danger: danger);
            item.Click += (sender, args) =>
            {
                if (!_building && !_quantityWriteBusy && menuRevision == _revision &&
                    _cachedStages.Any(stage => stage.Id == stageId && !stage.Rest))
                    onClick(sender, args);
            };
            StageMenu.Items.Add(item);
            return item;
        }
        Add($"Открыть КП «{stageName}»", "File", (_, _) => OpenStage(stageId));
        Add("Название…", "Edit", (_, _) => RenameStage(stageId), editableStage);
        Add("СМР, транспорт и сумма по договору…", "Edit", (_, _) => EditMoney(stageId), editableStage);
        Add("СМР по системам…", "List", (_, _) => EditSystemsMoney(stageId), editableStage);
        Add("Сумма по договору (для сверки)…", "Edit", (_, _) => SetTarget(stageId), editableStage);
        Add("Связанные ДС и КС…", "File", (_, _) => ShowLinks());
        StageMenu.Items.Add(new Separator());
        Add($"Весь остаток — в «{stageName}»", "Download", (_, _) => PutRest(stageId), editableStage);
        Add($"Очистить «{stageName}»", "X", (_, _) => ClearStage(stageId), editableStage, danger: true);
        Add($"Удалить «{stageName}»…", "Trash", (_, _) => DeleteStage(stageId, stageName), editableStage, danger: true);
        TableColumnMenu.AppendDynamicMenu(StagesGrid, StageMenu);
    }

    private DataGridColumn? ColumnUnderMouse(out bool isHeader)
    {
        isHeader = false;
        var point = System.Windows.Input.Mouse.GetPosition(StagesGrid);
        for (DependencyObject? node = StagesGrid.InputHitTest(point) as DependencyObject;
             node is not null && node != StagesGrid;)
        {
            if (node is DataGridCell cell) return cell.Column;
            if (node is System.Windows.Controls.Primitives.DataGridColumnHeader header)
            {
                isHeader = true;
                return header.Column;
            }
            node = node is System.Windows.Media.Visual || node is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    private void BuildStageRowsMenu()
    {
        var table = _table;
        var rows = StagesGrid.SelectedItems.OfType<DataRowView>().ToArray();
        if (table is null || rows.Length == 0 || rows.Length != StagesGrid.SelectedItems.Count ||
            rows.Any(row => !ReferenceEquals(row.Row.Table, table))) return;
        var ids = rows.Select(row => Convert.ToInt64(row.Row["SrcId"])).OrderBy(id => id).ToArray();
        if (ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Length) return;
        var revision = _revision;
        bool CanExecute() => FullEditable && !_closed && !_building && !_quantityWriteBusy &&
            !string.IsNullOrWhiteSpace(revision) && _revision == revision &&
            ReferenceEquals(_table, table) &&
            StagesGrid.SelectedItems.Count == rows.Length &&
            StagesGrid.SelectedItems.OfType<DataRowView>().All(row =>
                rows.Any(captured => ReferenceEquals(captured.Row, row.Row)));
        void Add(string title, string glyph, RoutedEventHandler click, bool enabled = true)
        {
            var item = ContextMenuIcons.Create(title, glyph, enabled && FullEditable);
            item.Click += (sender, args) =>
            {
                if (CanExecute()) click(sender, args);
                else SetStatus("Выбор или данные изменились. Откройте меню снова.");
            };
            StageMenu.Items.Add(item);
        }
        foreach (var stage in _cachedStages)
        {
            var id = stage.Id;
            Add($"Весь остаток выделенного — в «{ShortStageName(stage.Name)}»", "Download",
                (_, _) => ChangeSelectedStageRowsAsync(id, ids, true, revision),
                ids.Length > 0 && !stage.Locked && stage.StageSum is null && !stage.Rest);
        }
        if (_cachedStages.Count > 0) StageMenu.Items.Add(new Separator());
        foreach (var stage in _cachedStages)
        {
            var id = stage.Id;
            Add($"Убрать выделенное из «{ShortStageName(stage.Name)}»", "X",
                (_, _) => ChangeSelectedStageRowsAsync(id, ids, false, revision),
                ids.Length > 0 && !stage.Locked && stage.StageSum is null && !stage.Rest);
        }
        if (_cachedStages.Count > 0) StageMenu.Items.Add(new Separator());
        Add(_cachedStages.Count == 0 ? "Создать этап 1" : "Новый этап", "Plus", AddStage_Click);
    }

    private async void ChangeSelectedStageRowsAsync(long stageId, long[] ids,
        bool putRest, string revision)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || ids.Length == 0 ||
            string.IsNullOrWhiteSpace(revision) || revision != _revision ||
            !_cachedStages.Any(stage => stage.Id == stageId && !stage.Rest &&
                !stage.Locked && stage.StageSum is null)) return;
        _quantityWriteBusy = true;
        IsEnabled = false;
        try
        {
            var (saved, count) = await Task.Run(() => SqlServerStages.ChangeSelectedStageRows(
                _connectionString, _fullId, stageId, ids, putRest, revision));
            if (!saved.Saved)
                SetStatus("Операция отменена: " + saved.Error);
            else
                SetStatus(count == 0 ? "Нет строк для изменения." :
                    $"Изменено строк этапа: {count}.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Результат операции не подтверждён: " + ex.Message +
                ". Обновите окно перед повтором.");
            try { await ReloadAsync(); }
            catch (Exception reloadError)
            {
                _revision = "";
                SetStatus("Обновление не удалось: " + reloadError.Message +
                    ". Закройте и снова откройте окно этапов.");
            }
        }
        finally
        {
            IsEnabled = true;
            EndWrite();
        }
    }

    private async void AddStage_Click(object sender, RoutedEventArgs e)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = ServerRevision();
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.CreateStage(
                _connectionString, _fullId, revision));
            _revision = "";
            SetStatus("Этап создан. Количество строк — в таблице; цены — в открывшемся КП этапа.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void CreateRest_Click(object sender, RoutedEventArgs e)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = ServerRevision();
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.CreateRest(_connectionString, _fullId, revision));
            _revision = "";
            SetStatus("«Остаток» создан/обновлён: количества = полное КП − этапы.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void ShowLinks()
    {
        try
        {
            var revision = _revision;
            var links = await Task.Run(() =>
                SqlServerStages.ReadStageDocumentLinks(_connectionString, _fullId));
            if (_closed || _building || _quantityWriteBusy || revision != _revision) return;
            new StageLinksDialog(links) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            if (!_closed) SetStatus("Ошибка чтения связей документов: " + ex.Message);
        }
    }

    private async void EditMoney(long stageId)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        _quantityWriteBusy = true;
        try
        {
            var stage = await Task.Run(() =>
            {
                using var snapshotConnection = SqlServerDatabase.Open(_connectionString);
                if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                    throw new InvalidOperationException("Этапы изменились. Обновите окно.");
                var data = SqlServerStages.ReadStageMoney(snapshotConnection, stageId);
                if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                    throw new InvalidOperationException("Данные этапа изменились при чтении.");
                return data;
            });
            var dialog = new StageMoneyDialog(stage) { Owner = this };
            if (dialog.ShowDialog() != true || _revision != revision) return;
            var (smr, transport, target) = (dialog.Smr, dialog.Transport, dialog.ContractTarget);
            await Task.Run(() => SqlServerStages.SaveStageMoney(
                _connectionString, _fullId, stageId, smr, transport, target, revision));
            SetStatus("СМР, транспорт и сумма по договору сохранены.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка сохранения денег этапа: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void EditSystemsMoney(long stageId)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        _quantityWriteBusy = true;
        try
        {
            var data = await Task.Run(() =>
            {
                using var snapshotConnection = SqlServerDatabase.Open(_connectionString);
                if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                    throw new InvalidOperationException("Этапы изменились. Обновите окно.");
                var stage = SqlServerStages.ReadStageMoney(snapshotConnection, stageId);
                var systems = SqlServerStages.ReadStageSystems(snapshotConnection, stageId);
                if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                    throw new InvalidOperationException("Данные этапа изменились при чтении.");
                return (Stage: stage, Systems: systems);
            });
            var (stage, systems) = data;
            if (!stage.CanEdit || !stage.SignedStageExists)
            {
                SetStatus("Для СМР по системам нужен свободный этап и хотя бы один подписанный этап КП.");
                return;
            }
            if (systems.Count == 0)
            {
                SetStatus("Сначала распределите в этап позиции и системы.");
                return;
            }
            var dialog = new StageSystemsMoneyDialog(stage.Name, systems) { Owner = this };
            if (dialog.ShowDialog() != true || _revision != revision) return;
            var amounts = dialog.Amounts;
            await Task.Run(() => SqlServerStages.SaveStageSystems(
                _connectionString, _fullId, stageId, amounts, revision));
            SetStatus("СМР систем этапа сохранено.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка СМР по системам: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private bool StageCommandCurrent(SqlServerStages.StageInfo stage, string revision, bool allowTarget = false) =>
        FullEditable && !_closed && !_building && !_quantityWriteBusy &&
        !string.IsNullOrWhiteSpace(revision) && _revision == revision &&
        !stage.Rest && !stage.Locked && (allowTarget || stage.StageSum is null) &&
        _cachedStages.Any(item => ReferenceEquals(item, stage));

    private async void PutRest(long stageId)
    {
        var revision = _revision;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || !StageCommandCurrent(stage, revision)) return;
        if (MessageBox.Show(this, "Перенести весь положительный остаток полного КП в этот этап?",
                "Распределение этапов", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes || !StageCommandCurrent(stage, revision)) return;
        _quantityWriteBusy = true;
        try
        {
            var (saved, rowsCount) = await Task.Run(() => SqlServerStages.PutRest(
                _connectionString, _fullId, stageId, revision));
            if (!saved.Saved)
            {
                _revision = "";
                SetStatus("Ошибка переноса остатка: " + saved.Error);
                return;
            }
            _revision = saved.Revision;
            SetStatus($"Весь остаток перенесён: {rowsCount} строк(и).");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void ClearStage(long stageId)
    {
        var revision = _revision;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || !StageCommandCurrent(stage, revision)) return;
        if (MessageBox.Show(this, "Убрать из этапа все строки? Само КП этапа останется пустым.",
                "Очистить этап", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes || !StageCommandCurrent(stage, revision)) return;
        _quantityWriteBusy = true;
        try
        {
            var saved = await Task.Run(() => SqlServerStages.ClearStage(
                _connectionString, _fullId, stageId, revision));
            if (!saved.Saved)
            {
                _revision = "";
                SetStatus("Ошибка очистки этапа: " + saved.Error);
                return;
            }
            _revision = saved.Revision;
            SetStatus("Этап очищен.");
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void RenameStage(long stageId)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || stage.Locked || stage.StageSum is not null) return;
        var revision = _revision;
        var dialog = new PromptDialog("Название этапа", "Название", stage.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value.Trim().Length == 0 ||
            !StageCommandCurrent(stage, revision)) return;
        _quantityWriteBusy = true;
        try
        {
            var name = dialog.Value;
            await Task.Run(() => SqlServerStages.RenameStage(
                _connectionString, _fullId, stageId, name, revision));
            _revision = "";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void SetTarget(long stageId)
    {
        if (!FullEditable || _closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || !StageCommandCurrent(stage, revision, allowTarget: true)) return;
        var dialog = new PromptDialog("Сумма по договору", "Сумма для сверки (пусто — убрать):", stage.StageSum?.ToString(CultureInfo.CurrentCulture) ?? "") { Owner = this };
        if (dialog.ShowDialog() != true || !StageCommandCurrent(stage, revision, allowTarget: true)) return;
        decimal? sum = null;
        if (dialog.Value.Trim().Length > 0)
        {
            if (!decimal.TryParse(dialog.Value.Trim().Replace(',', '.'), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var parsed))
            {
                SetStatus("Недопустимое число: " + dialog.Value);
                return;
            }
            sum = parsed;
        }
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.SetStageTarget(
                _connectionString, stageId, sum, revision, _fullId));
            _revision = "";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    private async void DeleteStage(long stageId, string name)
    {
        var revision = _revision;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || stage.Name != name || !StageCommandCurrent(stage, revision)) return;
        if (MessageBox.Show(this, $"Удалить «{stage.Name}»? Копия сохранится в корзине (30 дней).\n" +
                "Распределение количеств и денег полного КП будет пересчитано. " +
                "При наличии зависимых документов удаление запрещено.",
                "Удаление этапа", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes || !StageCommandCurrent(stage, revision)) return;
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.DeleteStage(_connectionString, stageId, revision, _fullId));
            _revision = "";
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { EndWrite(); }
    }

    // Same operation handlers as the management table; inline menus must not
    // introduce a separate implementation of stage mutations.
    internal void InvokeInlineMenuAction(long stageId, string action, string expectedRevision)
    {
        if (!FullEditable || _closed || _quantityWriteBusy || _building || string.IsNullOrWhiteSpace(_revision) ||
            _revision != expectedRevision)
        {
            SetStatus("Распределение изменилось. Повторите действие из актуального окна этапов.");
            return;
        }
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || stage.Rest || stage.Locked || stage.StageSum is not null)
        {
            SetStatus("Этап недоступен для изменения.");
            return;
        }
        switch (action)
        {
            case "rename": RenameStage(stageId); break;
            case "money": EditMoney(stageId); break;
            case "systems": EditSystemsMoney(stageId); break;
            case "put": PutRest(stageId); break;
            case "clear": ClearStage(stageId); break;
            case "delete": DeleteStage(stageId, stage.Name); break;
        }
    }

    private void OpenStage(long stageId)
    {
        if (_closed || _building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        if (stageId is <= 0 or > int.MaxValue ||
            (!_cachedStages.Any(stage => stage.Id == stageId) && _restDocument?.Id != stageId))
        {
            SetStatus("Документ этапа не входит в текущий снимок или его идентификатор не поддерживается.");
            return;
        }
        // Открытие КП этапа — через главное окно (оно перечитает список и откроет смету).
        OpenStageRequested?.Invoke(checked((int)stageId));
        Close();
    }

    /// <summary>Главное окно подписывается: открыть смету этапа после закрытия окна этапов.</summary>
    public event Action<int>? OpenStageRequested;

    private void SetStatus(string message) => StatusText.Text = message;

    private string _revision = "";

    private string ServerRevision()
    {
        if (!string.IsNullOrWhiteSpace(_revision)) return _revision;
        throw new InvalidOperationException("Нет подтверждённой ревизии окна. Откройте этапы заново.");
    }
}
