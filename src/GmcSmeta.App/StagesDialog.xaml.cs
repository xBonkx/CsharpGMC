using System.Data;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
    private SqlServerStages.StageInfo? _restDocument;
    private List<SqlServerStages.StageInfo> _cachedStages = new();
    private List<SqlServerStages.StageTableRow> _cachedRows = new();

    public StagesDialog(string connectionString, int fullId, string fullTitle)
    {
        _connectionString = connectionString;
        _fullId = fullId;
        _fullTitle = fullTitle;
        InitializeComponent();
        CaptionText.Text = $"Этапы: {fullTitle}";
        Reload();
    }

    private void Reload()
    {
        _revision = "";
        _restDocument = null;
        List<SqlServerStages.StageInfo> stages;
        List<SqlServerStages.StageTableRow> rows;
        string revision;
        using (var conn = SqlServerDatabase.Open(_connectionString))
        {
            revision = SqlServerDatabase.Revision(conn);
            (stages, rows) = SqlServerStages.StageTable(conn, _fullId);
            var restDocument = SqlServerStages.StagesOf(conn, _fullId).FirstOrDefault(stage => stage.Rest);
            if (string.IsNullOrWhiteSpace(revision) ||
                revision != SqlServerDatabase.Revision(conn))
                throw new InvalidOperationException("Данные изменились при загрузке этапов. Откройте окно снова.");
            _restDocument = restDocument;
        }

        _building = true;
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
                readOnly: stage.Locked || stage.StageSum is not null);
            TableColumnMenu.SetStableId(column, "stage-" + stage.Id.ToString(CultureInfo.InvariantCulture));
            StagesGrid.Columns.Add(column);
            _columnStages[StagesGrid.Columns.IndexOf(column)] = stage.Id;
            stageIndex++;
        }
        var restColumn = MakeColumn("Остаток", "Rest", 92, readOnly: true);
        StagesGrid.Columns.Add(restColumn);

        _cachedStages = stages;
        _cachedRows = rows;
        FilterCachedRows();
        _revision = revision;
        TableColumnMenu.SetProtect(StagesGrid, "Раздел|Наименование|Всего|Остаток");
        TableColumnMenu.RefreshDynamic(StagesGrid, "stage-distribution-" + _fullId.ToString(CultureInfo.InvariantCulture), ownHeaderMenu: false);
    }

    /// <summary>Local search on the last loaded SQL snapshot (no DB roundtrip per key).</summary>
    private void FilterCachedRows()
    {
        if (StagesGrid is null) return;
        _building = true;
        var stages = _cachedStages;
        var rows = _cachedRows;
        var table = new DataTable();
        table.Columns.Add("SrcId", typeof(long));
        table.Columns.Add("SystemName", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Unit", typeof(string));
        table.Columns.Add("Qty", typeof(string));
        table.Columns.Add("__QtyExact", typeof(decimal));
        for (var i = 0; i < stages.Count; i++)
        {
            table.Columns.Add($"S{i}", typeof(string));
            table.Columns.Add($"__S{i}", typeof(decimal));
        }
        table.Columns.Add("Rest", typeof(string));
        table.Columns.Add("__RestExact", typeof(decimal));

        var words = SearchBox.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var row in rows)
        {
            var name = $"{row.SystemName} · {row.Name} · {row.Unit}";
            if (words.Length > 0 && !words.All(w => name.ToLowerInvariant().Contains(w))) continue;
            var record = table.NewRow();
            record["SrcId"] = row.SrcId;
            record["SystemName"] = row.SystemName;
            record["Name"] = row.Name;
            record["Unit"] = row.Unit;
            record["Qty"] = QtyText(row.Qty);
            record["__QtyExact"] = row.Qty;
            for (var i = 0; i < stages.Count; i++)
            {
                record[$"S{i}"] = row.PerStage[i] == 0m ? "" : QtyText(row.PerStage[i]);
                record[$"__S{i}"] = row.PerStage[i];
            }
            record["Rest"] = row.Rest == 0m ? "" : QtyText(row.Rest);
            record["__RestExact"] = row.Rest;
            table.Rows.Add(record);
        }
        StagesGrid.ItemsSource = table.DefaultView;
        _building = false;
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
        return new DataGridTextColumn
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
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => FilterCachedRows();

    private void StagesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (_building || _quantityWriteBusy || string.IsNullOrWhiteSpace(_revision) ||
            !_columnStages.ContainsKey(StagesGrid.Columns.IndexOf(e.Column)))
            e.Cancel = true;
    }

    private async void StagesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _building) return;
        if (_quantityWriteBusy) { e.Cancel = true; return; }
        if (!_columnStages.TryGetValue(StagesGrid.Columns.IndexOf(e.Column), out var stageId)) return;
        if (e.Row.Item is not DataRowView row) return;
        var srcId = Convert.ToInt64(row.Row["SrcId"]);
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
        _quantityWriteBusy = true;
        SetStatus("Сохранение…");
        try
        {
            var snapshotStageQty = columnIndex >= 0 && columnIndex < allocations.Length
                ? allocations[columnIndex] : (decimal?)null;
            var snapshotRest = (decimal)row.Row["__RestExact"];
            var result = await Task.Run(() => SqlServerStages.SetStageQty(
                _connectionString, stageId, srcId, value, ServerRevision(),
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
        try { Reload(); }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Не удалось обновить этапы: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
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
            var item = ContextMenuIcons.Create(rest is null ? "Создать документ «Остаток»" : "Открыть КП «Остаток»", rest is null ? "FilePlus" : "File");
            item.Click += (_, _) =>
            {
                if (_quantityWriteBusy || revision != _revision) return;
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
        var selectedStage = _cachedStages.FirstOrDefault(stage => stage.Id == stageId);
        var editableStage = selectedStage is not null &&
                            !selectedStage.Locked && selectedStage.StageSum is null;
        var menuRevision = _revision;
        MenuItem Add(string header, string glyph, RoutedEventHandler onClick, bool enabled = true, bool danger = false)
        {
            var item = ContextMenuIcons.Create(header, glyph, enabled, danger: danger);
            item.Click += (sender, args) =>
            {
                if (!_quantityWriteBusy && menuRevision == _revision) onClick(sender, args);
            };
            StageMenu.Items.Add(item);
            return item;
        }
        Add($"Открыть КП «{stageName}»", "File", (_, _) => OpenStage(stageId));
        Add("Название…", "Edit", (_, _) => RenameStage(stageId), editableStage);
        Add("СМР, транспорт и сумма по договору…", "Edit", (_, _) => EditMoney(stageId));
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
        var ids = StagesGrid.SelectedItems.OfType<DataRowView>()
            .Select(row => Convert.ToInt64(row.Row["SrcId"]))
            .Distinct().ToArray();
        var revision = _revision;
        void Add(string title, string glyph, RoutedEventHandler click, bool enabled = true)
        {
            var item = ContextMenuIcons.Create(title, glyph, enabled);
            item.Click += click;
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
        if (_building || _quantityWriteBusy || ids.Length == 0 ||
            string.IsNullOrWhiteSpace(revision) || revision != _revision) return;
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
            Reload();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Результат операции не подтверждён: " + ex.Message +
                ". Обновите окно перед повтором.");
            try { Reload(); }
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
            _quantityWriteBusy = false;
        }
    }

    private async void AddStage_Click(object sender, RoutedEventArgs e)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        try
        {
            var stageId = await Task.Run(() => SqlServerStages.CreateStage(
                _connectionString, _fullId, ServerRevision()));
            _revision = null;
            SetStatus("Этап создан. Количество строк — в таблице; цены — в открывшемся КП этапа.");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void CreateRest_Click(object sender, RoutedEventArgs e)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        try
        {
            await Task.Run(() => SqlServerStages.CreateRest(_connectionString, _fullId, ServerRevision()));
            _revision = null;
            SetStatus("«Остаток» создан/обновлён: количества = полное КП − этапы.");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
    }

    private async void ShowLinks()
    {
        try
        {
            var links = await Task.Run(() =>
                SqlServerStages.ReadStageDocumentLinks(_connectionString, _fullId));
            new StageLinksDialog(links) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка чтения связей документов: " + ex.Message);
        }
    }

    private async void EditMoney(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        _quantityWriteBusy = true;
        try
        {
            using var snapshotConnection = SqlServerDatabase.Open(_connectionString);
            if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                throw new InvalidOperationException("Этапы изменились. Обновите окно.");
            var stage = await Task.Run(() =>
                SqlServerStages.ReadStageMoney(_connectionString, stageId));
            if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                throw new InvalidOperationException("Данные этапа изменились при чтении.");
            var dialog = new StageMoneyDialog(stage) { Owner = this };
            if (dialog.ShowDialog() != true || _revision != revision) return;
            await Task.Run(() => SqlServerStages.SaveStageMoney(
                _connectionString, _fullId, stageId,
                dialog.Smr, dialog.Transport, dialog.ContractTarget, revision));
            SetStatus("СМР, транспорт и сумма по договору сохранены.");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка сохранения денег этапа: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void EditSystemsMoney(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        _quantityWriteBusy = true;
        try
        {
            using var snapshotConnection = SqlServerDatabase.Open(_connectionString);
            if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                throw new InvalidOperationException("Этапы изменились. Обновите окно.");
            var stage = await Task.Run(() =>
                SqlServerStages.ReadStageMoney(_connectionString, stageId));
            if (!stage.CanEdit || !stage.SignedStageExists)
            {
                SetStatus("Для СМР по системам нужен свободный этап и хотя бы один подписанный этап КП.");
                return;
            }
            var systems = await Task.Run(() =>
                SqlServerStages.ReadStageSystems(_connectionString, stageId));
            if (systems.Count == 0)
            {
                SetStatus("Сначала распределите в этап позиции и системы.");
                return;
            }
            if (SqlServerDatabase.Revision(snapshotConnection) != revision)
                throw new InvalidOperationException("Данные этапа изменились при чтении.");
            var dialog = new StageSystemsMoneyDialog(stage.Name, systems) { Owner = this };
            if (dialog.ShowDialog() != true || _revision != revision) return;
            await Task.Run(() => SqlServerStages.SaveStageSystems(
                _connectionString, _fullId, stageId, dialog.Amounts, revision));
            SetStatus("СМР систем этапа сохранено.");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка СМР по системам: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void PutRest(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        if (MessageBox.Show(this, "Перенести весь положительный остаток полного КП в этот этап?",
                "Распределение этапов", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes || revision != _revision) return;
        _quantityWriteBusy = true;
        try
        {
            var (saved, rowsCount) = await Task.Run(() => SqlServerStages.PutRest(
                _connectionString, _fullId, stageId, revision));
            if (!saved.Saved)
            {
                SetStatus("Ошибка переноса остатка: " + saved.Error);
                return;
            }
            _revision = saved.Revision;
            SetStatus($"Весь остаток перенесён: {rowsCount} строк(и).");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void ClearStage(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        if (MessageBox.Show(this, "Убрать из этапа все строки? Само КП этапа останется пустым.",
                "Очистить этап", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes || revision != _revision) return;
        _quantityWriteBusy = true;
        try
        {
            var saved = await Task.Run(() => SqlServerStages.ClearStage(
                _connectionString, _fullId, stageId, revision));
            if (!saved.Saved)
            {
                SetStatus("Ошибка очистки этапа: " + saved.Error);
                return;
            }
            _revision = saved.Revision;
            SetStatus("Этап очищен.");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void RenameStage(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || stage.Locked || stage.StageSum is not null) return;
        var revision = _revision;
        var dialog = new PromptDialog("Название этапа", "Название", stage.Name) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Value.Trim().Length == 0 || revision != _revision) return;
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.RenameStage(
                _connectionString, _fullId, stageId, dialog.Value, revision));
            _revision = "";
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void SetTarget(long stageId)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null) return;
        var dialog = new PromptDialog("Сумма по договору", "Сумма для сверки (пусто — убрать):", stage.StageSum?.ToString(CultureInfo.CurrentCulture) ?? "") { Owner = this };
        if (dialog.ShowDialog() != true || _revision != revision) return;
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
                _connectionString, stageId, sum, revision));
            _revision = "";
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    private async void DeleteStage(long stageId, string name)
    {
        if (_quantityWriteBusy || string.IsNullOrWhiteSpace(_revision)) return;
        var revision = _revision;
        if (MessageBox.Show(this, $"Удалить «{name}»? Копия сохранится в корзине (30 дней).",
                "Удаление этапа", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes || revision != _revision) return;
        _quantityWriteBusy = true;
        try
        {
            await Task.Run(() => SqlServerStages.DeleteStage(_connectionString, stageId, revision));
            _revision = "";
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message);
        }
        finally { _quantityWriteBusy = false; }
    }

    // Same operation handlers as the management table; inline menus must not
    // introduce a separate implementation of stage mutations.
    internal void InvokeInlineMenuAction(long stageId, string action, string expectedRevision)
    {
        if (_quantityWriteBusy || _building || string.IsNullOrWhiteSpace(_revision) ||
            _revision != expectedRevision)
        {
            SetStatus("Распределение изменилось. Повторите действие из актуального окна этапов.");
            return;
        }
        var stage = _cachedStages.FirstOrDefault(item => item.Id == stageId);
        if (stage is null || stage.Locked || stage.StageSum is not null)
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
        // Открытие КП этапа — через главное окно (оно перечитает список и откроет смету).
        OpenStageRequested?.Invoke((int)stageId);
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
