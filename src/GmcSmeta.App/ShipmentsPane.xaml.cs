using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Идентичность группы не зависит от совпадения названий систем и документов.</summary>
public sealed record ShipmentSystemGroup(long ContractId, long SystemId, string Label);

/// <summary>Ведомость в окне выдачи — отображение строки и её итогов.</summary>
public sealed class ShipmentPreviewRow
{
    public long ContractId { get; init; }
    public long SystemId { get; init; }
    public bool IsSub { get; init; }
    public long PositionId { get; init; }
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; set; } = "";
    public string Price { get; init; } = "";
    public string IssuedTotal { get; init; } = "";
    public string SystemLabel { get; init; } = "";
    public ShipmentSystemGroup SystemGroup => new(ContractId, SystemId, SystemLabel);
    public string ByEstimate { get; init; } = "";
    public string StockText { get; init; } = "";
    public string LeftText { get; init; } = "";
    public bool InShipment { get; init; }
    public bool CanChange { get; init; }
    public decimal Remaining { get; init; }
    public decimal Stock { get; init; }
    public decimal Suggested { get; init; }
    public decimal RecordedQty { get; init; }
    public bool StockOverrun { get; init; }
    public bool Planned { get; init; }
    public bool FullyIssued { get; init; }
    public string QtyHint => !CanChange ? "" : StockOverrun
        ? $"Выдаём больше, чем есть на складе (на складе {(string.IsNullOrEmpty(StockText) ? "0" : StockText)})"
        : InShipment ? "Поправить — двойной щелчок" : "Двойной щелчок — вписать, сколько выдаём";
}

/// <summary>
/// «Выдача на объект» (ship_view.py): ведомости объекта — создание, правка количеств,
/// фиксация (каждая строка становится движением «выдано на объект») и отмена.
/// Добавление строк — отметки строк объекта: со склада или остаток к выдаче.
/// </summary>
public partial class ShipmentsPane : UserControl
{
    private readonly string _connectionString;
    private readonly int _objectId;
    private string _revision = "";
    private long _shipmentId;
    private bool _building;
    private long _loadGeneration;
    private long _attachmentGeneration;
    private bool _needsReloadAfterAttach = true;
    private ObjectSnapshot? _objectSnapshot;
    private bool _shipmentSnapshotMayReuse;
    private bool _quantityBusy;
    private long _editingFixedId;
    private bool IsEditingFixed => Current is { Fixed: true } current && current.Id == _editingFixedId;
    private bool IsEditable => Current is { Fixed: false } || IsEditingFixed;
    private ObservableCollection<ShipmentPreviewRow> _rows = new();
    private readonly HashSet<(long Contract, long System)> _collapsedGroups = new();
    private bool _restoringGroup;
    private int _viewportGeneration;
    private sealed record GridState(HashSet<long> SelectedIds, long? CurrentId,
        DataGridColumn? Column, double VerticalOffset, double HorizontalOffset);

    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindVisual<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private ScrollViewer? GridScrollViewer() => FindVisual<ScrollViewer>(LinesGrid);

    private GridState CaptureGridState()
    {
        var scroll = GridScrollViewer();
        return new GridState(LinesGrid.SelectedItems.OfType<ShipmentPreviewRow>()
            .Select(row => row.PositionId).ToHashSet(),
            (LinesGrid.CurrentCell.Item as ShipmentPreviewRow)?.PositionId,
            LinesGrid.CurrentCell.Column, scroll?.VerticalOffset ?? 0, scroll?.HorizontalOffset ?? 0);
    }

    private void RestoreGridState(GridState state)
    {
        if (LinesGrid.ItemsSource is not ListCollectionView view) return;
        var selected = view.OfType<ShipmentPreviewRow>()
            .Where(row => state.SelectedIds.Contains(row.PositionId)).ToList();
        LinesGrid.SelectedItems.Clear();
        foreach (var row in selected) LinesGrid.SelectedItems.Add(row);
        if (state.CurrentId.HasValue && state.Column is not null &&
            LinesGrid.Columns.Contains(state.Column) &&
            view.OfType<ShipmentPreviewRow>().FirstOrDefault(row => row.PositionId == state.CurrentId.Value) is { } current)
            LinesGrid.CurrentCell = new DataGridCellInfo(current, state.Column);
        var generation = ++_viewportGeneration;
        var shipmentId = _shipmentId;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded || generation != _viewportGeneration || shipmentId != _shipmentId ||
                GridScrollViewer() is not { } scroll) return;
            scroll.ScrollToVerticalOffset(state.VerticalOffset);
            scroll.ScrollToHorizontalOffset(state.HorizontalOffset);
        }));
    }

    private void ApplyGroupState(Expander expander)
    {
        if (expander.DataContext is not CollectionViewGroup { Name: ShipmentSystemGroup group }) return;
        _restoringGroup = true;
        try { expander.IsExpanded = !_collapsedGroups.Contains((group.ContractId, group.SystemId)); }
        finally { _restoringGroup = false; }
    }

    private void SystemGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander) ApplyGroupState(expander);
    }

    private void SystemGroup_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Expander expander) ApplyGroupState(expander);
    }

    private void SystemGroup_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringGroup || !ReferenceEquals(sender, e.OriginalSource) ||
            sender is not Expander { IsLoaded: true, DataContext: CollectionViewGroup { Name: ShipmentSystemGroup group } } expander)
            return;
        var key = (group.ContractId, group.SystemId);
        if (expander.IsExpanded) _collapsedGroups.Remove(key);
        else _collapsedGroups.Add(key);
    }

    public ShipmentsPane(string connectionString, int objectId, string objectName)
    {
        _connectionString = connectionString;
        _objectId = objectId;
        InitializeComponent();
        CaptionText.Text = $"Выдача на объект: {objectName}";
        Loaded += (_, _) => ResumeShipmentReadIfNeeded();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ResumeShipmentReadIfNeeded();
            else CancelOrdinaryShipmentRead();
        };
        Unloaded += (_, _) =>
        {
            ++_attachmentGeneration;
            CancelOrdinaryShipmentRead();
            ++_loadGeneration;
            ++_viewportGeneration;
            _revision = "";
            _objectSnapshot = null;
            _shipmentSnapshotMayReuse = false;
            _needsReloadAfterAttach = true;
            _building = false;
        };
    }

    // Revision belongs to the displayed snapshot, never refresh it just before a write.
    private string ServerRevision() => _revision;
    private bool CanStartWrite => IsLoaded && !_building && !_quantityBusy && !string.IsNullOrEmpty(_revision);
    internal bool HasCacheActivity => _quantityBusy || _building || HasHeaderDraft() ||
        (LinesGrid.ItemsSource is System.ComponentModel.IEditableCollectionView { IsEditingItem: true });

    public void RefreshFromSqlServer()
    {
        if (IsLoaded && !_quantityBusy && !_building && !HeaderDirty() &&
            LinesGrid.ItemsSource is not ListCollectionView { IsEditingItem: true })
        {
            _ = ReloadAsync(requiredReadback: false);
        }
    }

    internal void RefreshOnTabReturn()
    {
        if (IsLoaded && !_quantityBusy && !_building && !HasHeaderDraft() &&
            LinesGrid.ItemsSource is not ListCollectionView { IsEditingItem: true })
            _ = ReloadAsync(allowUnchanged: true, requiredReadback: false);
    }

    internal static bool CanReuseShipmentSnapshot(string? objectRevision, string displayedRevision,
        string observedRevision, long displayedId, long requestedId, bool requiresReadback) =>
        !requiresReadback && displayedId == requestedId &&
        !string.IsNullOrWhiteSpace(displayedRevision) &&
        string.Equals(objectRevision, displayedRevision, StringComparison.Ordinal) &&
        string.Equals(displayedRevision, observedRevision, StringComparison.Ordinal);

    private List<SqlServerShipments.ShipmentInfo> _shipments = new();

    private void ClearShipment(string message)
    {
        _shipmentId = _editingFixedId = 0;
        ++_viewportGeneration;
        LinesGrid.ItemsSource = null;
        _rows.Clear();
        FixedCheck.IsChecked = false;
        FixedCheck.IsEnabled = RecipientBox.IsEnabled = IssuedByBox.IsEnabled = false;
        AddRowsButton.IsEnabled = StockRowsButton.IsEnabled = RemoveRowsButton.IsEnabled = DeleteShipmentButton.IsEnabled = false;
        DocumentFilter.Visibility = Visibility.Collapsed;
        SyncShipmentFilter(hasShipment: false, editable: false);
        QtyColumn.IsReadOnly = true;
        NumberBox.Text = RecipientBox.Text = IssuedByBox.Text = "";
        DateBox.SelectedDate = null;
        NumberBox.IsEnabled = DateBox.IsEnabled = SaveHeaderButton.IsEnabled = false;
        EditFixedButton.Visibility = FinishFixedButton.Visibility = Visibility.Collapsed;
        ExportExcelButton.IsEnabled = ExportPdfButton.IsEnabled = false;
        LinesTile.Text = OverTile.Text = TotalTile.Text = "—";
        StockWarningText.Text = "";
        StockWarningText.Visibility = ShipmentHelpText.Visibility = Visibility.Collapsed;
        TotalTile.ToolTip = null;
        UpdateShipmentSteps(null);
        SetStatus(message);
    }

    private sealed record ShipmentItem(long Id, SqlServerShipments.ShipmentInfo Info)
    {
        public string Title => $"№ {Info.Number}  ·  {Info.Date:dd.MM.yyyy}";
        public bool HasRecipient => !string.IsNullOrWhiteSpace(Info.Recipient);
        public string RecipientCaption => HasRecipient ? Info.Recipient : "кому — не указано";
        public string StateCaption => Info.Fixed ? "✓ Выдано" : "Готовится";
        public string LineCountCaption => ShipmentCardText.PositionCount(Info.Lines);
        public string Details => RecipientCaption + "\n" + StateCaption + " · " + LineCountCaption;
        public override string ToString() => Title + " · " + Details;
    }

    private SqlServerShipments.ShipmentInfo? Current =>
        ShipmentCombo.SelectedItem is ShipmentItem item ? item.Info : null;

    private void ApplyShipment(ShipmentSnapshot snapshot, GridState? gridState)
    {
        var current = Current;
        if (current is null || current.Id != snapshot.ShipmentId)
            throw new InvalidOperationException("Выбранная ведомость изменилась.");
        _shipmentId = current.Id;
        if (!current.Fixed || _editingFixedId != current.Id) _editingFixedId = 0;
        FixedCheck.IsChecked = current.Fixed;
        NumberBox.Text = current.Number;
        DateBox.SelectedDate = current.Date.Date;
        RecipientBox.Text = current.Recipient;
        IssuedByBox.Text = current.IssuedBy;
        var editable = IsEditable;
        NumberBox.IsEnabled = DateBox.IsEnabled = SaveHeaderButton.IsEnabled = editable;
        EditFixedButton.Visibility = current.Fixed && !IsEditingFixed ? Visibility.Visible : Visibility.Collapsed;
        FinishFixedButton.Visibility = IsEditingFixed ? Visibility.Visible : Visibility.Collapsed;
        ExportExcelButton.IsEnabled = ExportPdfButton.IsEnabled = current.Lines > 0;
        QtyColumn.IsReadOnly = !editable;
        RecipientBox.IsEnabled = editable;
        IssuedByBox.IsEnabled = editable;
        AddRowsButton.IsEnabled = editable;
        // Keep the menu available for a posted shipment so it can explain the restriction.
        DeleteShipmentButton.IsEnabled = true;
        FixedCheck.IsEnabled = !IsEditingFixed && (current.Fixed || current.Lines > 0);
        SyncShipmentFilter(hasShipment: true, editable: editable);
        StockRowsButton.IsEnabled = RemoveRowsButton.IsEnabled = editable;
        var documents = snapshot.Object.Documents;
        var documentId = (DocumentFilter.SelectedItem as SqlServerShipments.ShipmentDocument)?.Id ?? 0;
        var documentOptions = new List<SqlServerShipments.ShipmentDocument> { new(0, "Все документы объекта") };
        documentOptions.AddRange(documents);
        DocumentFilter.ItemsSource = documentOptions;
        DocumentFilter.SelectedItem = documentOptions.FirstOrDefault(d => d.Id == documentId) ?? documentOptions[0];
        DocumentFilter.Visibility = editable && documents.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _rows = new ObservableCollection<ShipmentPreviewRow>(snapshot.Rows);
        var view = new ListCollectionView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ShipmentPreviewRow.SystemGroup)));
        view.Filter = RowVisible;
        LinesGrid.ItemsSource = view;
        if (gridState is not null) RestoreGridState(gridState);
        var over = UpdateSummary(snapshot.LineCount);
        UpdateShipmentSteps(current);
        FooterText.Text = IsEditingFixed
            ? "Изменяете выданную ведомость — правки сразу проводятся в закупках."
            : current.Fixed
            ? "Ведомость выдана. Нажмите «Изменить» для правок или снимите отметку для отмены выдачи."
            : "Заполните количества; «Ведомость выдана» проведёт движения по каждой строке." +
                (over > 0 ? $" Внимание: {over} поз. превышают складской остаток." : "");
    }

    private static ShipmentPreviewRow BuildPreviewRow(SqlServerShipments.IssuableRow row,
        SqlServerShipments.ShipmentLineRow? line, SqlServerShipments.ShipmentInfo current,
        bool editable, string systemLabel)
    {
        var now = line?.Qty ?? 0m;
        var stock = Math.Max(0m, row.Received - row.Issued);
        var remaining = ShipmentQuantityPolicy.AvailableForChange(row.Qty, row.Issued, current.Fixed ? now : 0m);
        var before = row.Issued - (current.Fixed ? now : 0m);
        var left = row.Qty - row.Issued - (current.Fixed ? 0m : now);
        return new ShipmentPreviewRow
        {
            ContractId = row.ContractId,
            SystemId = row.SystemId,
            IsSub = row.IsSub,
            PositionId = row.PositionId,
            Name = row.Name + (row.OutOfKp ? "  [вне КП]" : ""),
            Unit = row.Unit,
            SystemLabel = systemLabel,
            ByEstimate = Num(row.Qty),
            Qty = now == 0m ? "" : Num(now),
            Price = Money.Format(row.Price),
            IssuedTotal = before > 0.000001m ? Num(before) : "",
            StockText = stock > 0m ? Num(stock) : "",
            LeftText = Math.Abs(left) <= 0.000001m ? "всё" : Num(left),
            InShipment = line is not null,
            CanChange = editable,
            RecordedQty = now,
            StockOverrun = !current.Fixed && ShipmentQuantityPolicy.ExceedsStock(row.Received, row.Issued, now),
            Remaining = remaining,
            Stock = stock,
            Suggested = ShipmentQuantityPolicy.SuggestedFromStock(row.Qty, row.Issued, row.Received),
            Planned = row.Qty > 0.000001m,
            FullyIssued = row.Qty - row.Issued <= 0.000001m,
        };
    }

    private int UpdateSummary(int lineCount)
    {
        var over = 0;
        var planned = 0;
        var done = 0;
        foreach (var row in _rows)
        {
            if (row.StockOverrun) over++;
            if (!row.Planned) continue;
            planned++;
            if (row.FullyIssued) done++;
        }
        LinesTile.Text = ShipmentCardText.PositionCount(lineCount);
        OverTile.Text = over == 0 ? "нет" : ShipmentCardText.PositionCount(over);
        StockWarningText.Text = over == 0 ? "" :
            $"⚠ {ShipmentCardText.PositionCount(over)}: выдаём больше, чем есть на складе";
        StockWarningText.Visibility = over == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShipmentHelpText.Visibility = IsEditable ? Visibility.Visible : Visibility.Collapsed;
        TotalTile.Text = planned == 0 ? "нет позиций" :
            $"выдано {100m * done / planned:0}% · осталось {ShipmentCardText.PositionCount(planned - done)}";
        TotalTile.ToolTip = $"Выдано полностью {done} из {planned} позиций всех документов объекта";
        return over;
    }

    private async Task<bool> TryApplyChangedRowsAsync(IReadOnlyList<long> positionIds, string committedRevision)
    {
        if (!IsLoaded || _needsReloadAfterAttach || positionIds.Count == 0 || positionIds.Count > SqlServerShipments.MaxIncrementalRows ||
            Current is not { } current || LinesGrid.ItemsSource is not ListCollectionView { IsEditingItem: false })
            return false;
        var attachment = _attachmentGeneration;
        var changes = await Task.Run(() => SqlServerShipments.ReadChangedRows(
            _connectionString, _objectId, current.Id, positionIds, committedRevision));
        if (!IsLoaded || attachment != _attachmentGeneration || changes is null || Current?.Id != current.Id ||
            (changes.Shipment with { Lines = current.Lines }) != current) return false;

        var indexById = _rows.Select((row, index) => (row.PositionId, Index: index))
            .ToDictionary(pair => pair.PositionId, pair => pair.Index);
        var byId = changes.Lines.ToDictionary(line => line.PositionId);
        var replacements = new List<(int Index, ShipmentPreviewRow Row)>();
        var expectedLines = current.Lines;
        foreach (var candidate in changes.Candidates)
        {
            if (!indexById.TryGetValue(candidate.PositionId, out var index)) return false;
            var old = _rows[index];
            if (old.SystemId != candidate.SystemId || old.ContractId != candidate.ContractId) return false;
            byId.TryGetValue(candidate.PositionId, out var line);
            var replacement = BuildPreviewRow(candidate, line, changes.Shipment, IsEditable, old.SystemLabel);
            expectedLines += (replacement.InShipment ? 1 : 0) - (old.InShipment ? 1 : 0);
            replacements.Add((index, replacement));
        }
        var cardIndex = _shipments.FindIndex(item => item.Id == current.Id);
        if (replacements.Count != positionIds.Count || expectedLines != changes.Shipment.Lines || cardIndex < 0)
            return false;

        var state = CaptureGridState();
        _building = true;
        try
        {
            foreach (var (index, row) in replacements) _rows[index] = row;
            _shipments[cardIndex] = changes.Shipment;
            ShipmentCombo.Items[cardIndex] = new ShipmentItem(current.Id, changes.Shipment);
            ShipmentCombo.SelectedIndex = cardIndex;
            ExportExcelButton.IsEnabled = ExportPdfButton.IsEnabled = changes.Shipment.Lines > 0;
            FixedCheck.IsEnabled = !IsEditingFixed && (current.Fixed || changes.Shipment.Lines > 0);
            UpdateSummary(changes.Shipment.Lines);
            UpdateShipmentSteps(changes.Shipment);
            RestoreGridState(state);
            _objectSnapshot = null; // incremental writes invalidate object-wide stock/header cache
            _shipmentSnapshotMayReuse = false;
            _revision = changes.Revision;
            return true;
        }
        finally { _building = false; }
    }

    private static string Num(decimal v) =>
        ShipmentQuantityPolicy.Format(v, CultureInfo.CurrentCulture);

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_quantityBusy || _building || LinesGrid.ItemsSource is ListCollectionView { IsEditingItem: true }) return;
        if (HeaderDirty() && !Ask("Обновление", "Отбросить несохранённые реквизиты?")) return;
        await ReloadAsync(requiredReadback: false);
    }

    private async void Shipment_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _building) return;
        if (_quantityBusy || HeaderDirty() ||
            LinesGrid.ItemsSource is ListCollectionView { IsEditingItem: true })
        {
            // A pending cell commit reserves the operation before Dispatcher.Yield.
            // WPF may still raise selection events in that interval.
            _building = true;
            try { ShipmentCombo.SelectedIndex = _shipments.FindIndex(s => s.Id == _shipmentId); }
            finally { _building = false; }
            if (!_quantityBusy) SetStatus("Сначала сохраните реквизиты или обновите ведомость с отменой правок.");
            return;
        }
        _editingFixedId = 0;
        await ReloadAsync(requestedId: Current?.Id ?? 0, reuseObject: true, requiredReadback: false);
    }

    private async void NewShipment_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !RequireSavedHeader()) return;
        var revision = ServerRevision();
        await RunOperationAsync(async () =>
        {
            _shipmentId = await Task.Run(() => SqlServerShipments.Create(
                _connectionString, _objectId, revision));
            return new OperationOutcome("Ведомость создана.");
        });
    }

    private void ShipmentMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !CanStartWrite || Current is not { } current) return;
        var id = current.Id;
        var revision = ServerRevision();
        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        var canDelete = !current.Fixed && !HeaderDirty();
        var delete = ContextMenuIcons.Create("Удалить ведомость…", "Trash", canDelete, danger: true);
        delete.ToolTip = current.Fixed
            ? "Ведомость уже выдана. Сначала снимите отметку «Ведомость выдана (фиксация)»."
            : !canDelete
                ? "Сначала сохраните реквизиты ведомости."
                : "Удалить текущую черновую ведомость после подтверждения.";
        ToolTipService.SetShowOnDisabled(delete, true);
        delete.Click += (_, _) =>
        {
            if (CanStartWrite && Current is { Fixed: false } selected &&
                selected.Id == id && ServerRevision() == revision)
                DeleteShipment_Click(sender, new RoutedEventArgs());
            else
                SetStatus("Ведомость или данные изменились. Откройте меню заново.");
        };
        menu.Items.Add(delete);
        menu.IsOpen = true;
    }

    private async void DeleteShipment_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !RequireSavedHeader() || Current is not { Fixed: false } current) return;
        var revision = ServerRevision();
        await WriteQuantityAsync(() => SqlServerShipments.DeleteList(
            _connectionString, current.Id, revision), "Ведомость удалена.",
            confirm: () => Ask("Удаление ведомости", $"Удалить {SqlServerShipments.TitleOf(current.Number)}?"));
    }

    // Compare with the loaded header, not a newly selected ListBox item.
    private bool HeaderDirty() => !string.IsNullOrEmpty(_revision) && HasHeaderDraft();

    // Unloading invalidates the revision, but does not make unsaved text disposable.
    private bool HasHeaderDraft()
    {
        var loaded = _shipments.FirstOrDefault(s => s.Id == _shipmentId);
        return loaded is not null && (!loaded.Fixed || _editingFixedId == loaded.Id) &&
            (NumberBox.Text.Trim() != loaded.Number || DateBox.SelectedDate?.Date != loaded.Date.Date ||
             RecipientBox.Text.Trim() != loaded.Recipient || IssuedByBox.Text.Trim() != loaded.IssuedBy);
    }

    private bool RequireSavedHeader()
    {
        if (!HeaderDirty()) return true;
        SetStatus("Сначала сохраните реквизиты ведомости кнопкой «Сохранить реквизиты».");
        return false;
    }

    private void Header_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SaveHeader_Click(sender, e); }
    }

    private async void SaveHeader_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !IsEditable || Current is not { } current || !HeaderDirty()) return;
        var number = NumberBox.Text.Trim();
        var recipient = RecipientBox.Text.Trim();
        var issuedBy = IssuedByBox.Text.Trim();
        if (number.Length == 0 || DateBox.SelectedDate is not { } date)
        {
            SetStatus("Укажите номер и корректную дату ведомости.");
            return;
        }
        var revision = ServerRevision();
        await WriteQuantityAsync(() => SqlServerShipments.SaveDetails(
            _connectionString, current.Id, number, date, recipient, issuedBy, current.Fixed, revision),
            "Реквизиты сохранены.");
    }

    private async void EditFixed_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || Current is not { Fixed: true } current) return;
        if (!Ask("Изменить выданную ведомость", "Изменения сразу отразятся в складских движениях. Продолжить?")) return;
        _editingFixedId = current.Id;
        _requestedRowFilter = 0;
        await ReloadAsync(reuseObject: true, requiredReadback: false);
    }

    private async void FinishFixed_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !IsEditingFixed || !RequireSavedHeader()) return;
        _editingFixedId = 0;
        await ReloadAsync(reuseObject: true, requiredReadback: false);
    }

    private bool Ask(string title, string message) =>
        MessageBox.Show(Window.GetWindow(this), message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private async void Fixed_Changed(object sender, RoutedEventArgs e)
    {
        if (_building || Current is not { } current) return;
        var fix = FixedCheck.IsChecked == true;
        if (fix == current.Fixed) return;
        // Checkbox represents persisted state, including on cancellation or a failed write.
        _building = true;
        try { FixedCheck.IsChecked = current.Fixed; }
        finally { _building = false; }
        if (!CanStartWrite || IsEditingFixed || !RequireSavedHeader()) return;
        var revision = ServerRevision();
        if (fix)
        {
            if (current.Lines == 0)
            {
                SetStatus("Ведомость пустая — добавьте позиции перед выдачей.");
                return;
            }
            var over = _rows.Count(row => row.StockOverrun);
            var message = $"Выдать на объект {current.Lines} поз. " +
                $"({current.Date:dd.MM.yyyy})?\n\nПозиции спишутся со склада и отразятся в закупках.";
            if (over > 0) message += $"\n\nВнимание: {over} поз. — больше, чем есть на складе.";
            await WriteQuantityAsync(() => SqlServerShipments.Fix(
                _connectionString, current.Id, revision), "Ведомость проведена.",
                confirm: () => (!string.IsNullOrWhiteSpace(current.Recipient) ||
                    Ask("Кому выдаём?", "Получатель не указан. Всё равно выдать?")) &&
                    Ask("Выдать на объект", message));
        }
        else
            await WriteQuantityAsync(() => SqlServerShipments.Unfix(
                _connectionString, current.Id, revision), "Выдача отменена: позиции возвращены на склад.",
                confirm: () => Ask("Отменить выдачу", "Отменить выдачу по этой ведомости?\n\nПозиции вернутся на склад."));
    }

    private bool RowVisible(object item)
    {
        if (item is not ShipmentPreviewRow row) return false;
        var filter = EffectiveShipmentFilter;
        if (filter == 2 && !row.InShipment) return false;
        // Python: «В ведомости» всегда показывает строки всех документов объекта.
        if (filter != 2 && DocumentFilter.SelectedItem is SqlServerShipments.ShipmentDocument { Id: > 0 } document &&
            row.ContractId != document.Id) return false;
        if (filter == 1 && !row.InShipment && (row.Stock <= 0.000001m || row.Remaining <= 0.000001m)) return false;
        var words = RowSearch.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(w => (row.Name + " " + row.SystemLabel).Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    private void RowFilter_Changed(object sender, SelectionChangedEventArgs e) => RefreshRowFilter();
    private void RowSearch_Changed(object sender, TextChangedEventArgs e) => RefreshRowFilter();

    private void ClearShipmentSearch_Click(object sender, RoutedEventArgs e)
    {
        RowSearch.Clear();
        RowSearch.Focus();
    }

    private void RefreshRowFilter()
    {
        if (!_building && !_quantityBusy && LinesGrid?.ItemsSource is ListCollectionView { IsEditingItem: false } view)
        {
            var state = CaptureGridState();
            view.Refresh();
            RestoreGridState(state);
        }
    }

    // Select only visible rows of this exact group, then reuse the existing atomic
    // add/stock/remove actions. Never select another system with the same caption.
    private void SelectSystem_Click(object sender, RoutedEventArgs e)
    {
        if (_building || _quantityBusy || sender is not Button { DataContext: CollectionViewGroup group }) return;
        var rows = group.Items.OfType<ShipmentPreviewRow>().Where(RowVisible).ToList();
        if (rows.Count == 0) return;
        LinesGrid.SelectedItems.Clear();
        foreach (var row in rows) LinesGrid.SelectedItems.Add(row);
        SetStatus($"Выбрана система: {rows.Count} поз. Можно добавить или убрать выбранные строки.");
    }

    private async void AddRows_Click(object sender, RoutedEventArgs e) => await AddVisibleRows(false);
    private async void StockRows_Click(object sender, RoutedEventArgs e) => await AddVisibleRows(true);

    private async void RemoveRows_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !IsEditable || !RequireSavedHeader() || Current is not { } current) return;
        var ids = LinesGrid.SelectedItems.OfType<ShipmentPreviewRow>()
            .Where(row => row.InShipment && row.CanChange && RowVisible(row))
            .Select(row => row.PositionId).Distinct().ToArray();
        if (ids.Length == 0)
        {
            SetStatus("Выделите строки, которые нужно убрать из ведомости.");
            return;
        }
        var revision = ServerRevision();
        await WriteQuantityAsync(() => SqlServerShipments.RemoveRowsBatch(
            _connectionString, current.Id, ids, revision, current.Fixed), $"Убрано {ids.Length} строк одной транзакцией.",
            changedPositions: ids);
    }

    private async Task AddVisibleRows(bool onlyStock)
    {
        if (!CanStartWrite || !IsEditable || !RequireSavedHeader() || Current is not { } current) return;
        var visible = (LinesGrid.ItemsSource as ListCollectionView)?.OfType<ShipmentPreviewRow>().ToList() ?? new();
        var selected = LinesGrid.SelectedItems.OfType<ShipmentPreviewRow>().Where(RowVisible).ToList();
        var source = selected.Count > 0 ? selected : visible;
        var entries = source.Where(r => r.CanChange && !r.InShipment)
            .Select(r => (PositionId: r.PositionId,
                Qty: onlyStock ? Math.Min(r.Stock, r.Remaining) : r.Suggested))
            .Where(r => r.Qty > 0.000001m).ToList();
        if (entries.Count == 0)
        {
            SetStatus("Нет доступных позиций для добавления.");
            return;
        }
        var revision = ServerRevision();
        await WriteQuantityAsync(() => SqlServerShipments.AddRowsBatch(
            _connectionString, current.Id, entries, revision, current.Fixed), $"Добавлено {entries.Count} строк одной транзакцией.",
            confirm: () => selected.Count > 0 || Ask("Добавление в ведомость",
                $"Добавить {entries.Count} позиций из всех отфильтрованных строк?"),
            changedPositions: entries.Select(entry => entry.PositionId).ToArray());
    }

    private async void MarkRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox check || check.DataContext is not ShipmentPreviewRow row) return;
        // The checkbox reflects committed state only. Reload it on success or failure.
        check.IsChecked = row.InShipment;
        if (!CanStartWrite || !IsEditable || !RequireSavedHeader() || !row.CanChange || Current is not { } current) return;
        var qty = row.InShipment ? 0m : row.Suggested;
        if (!row.InShipment && qty <= 0.000001m)
        {
            SetStatus("По этой позиции всё уже выдано.");
            return;
        }
        var revision = ServerRevision();
        await WriteQuantityAsync(() => SqlServerShipments.SetQty(
            _connectionString, current.Id, row.PositionId, qty, false, revision, current.Fixed),
            row.InShipment ? "Позиция убрана из ведомости." : "Позиция добавлена в ведомость.",
            changedPositions: new[] { row.PositionId });
    }

    private void LinesGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (!CanStartWrite || !IsEditable || !RequireSavedHeader() ||
            !ReferenceEquals(e.Column, QtyColumn) || e.Row.Item is not ShipmentPreviewRow { CanChange: true })
            e.Cancel = true;
        else
            _shipmentSnapshotMayReuse = false; // TwoWay input may survive even a cancelled edit.
    }

    private async void LinesGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || _building) return;
        if (!CanStartWrite || !IsEditable || !RequireSavedHeader()) { e.Cancel = true; return; }
        if (e.Row.Item is not ShipmentPreviewRow { CanChange: true } row ||
            Current is not { } current) return;
        // Do not dispatch by a translated header ("Кол-во" was renamed).
        if (!ReferenceEquals(e.Column, QtyColumn) || e.EditingElement is not TextBox editor) return;
        var input = editor.Text.Trim();
        if (!ShipmentQuantityPolicy.TryParseEdit(input, out var qty))
        {
            SetStatus("Количество: 0 для удаления или больше 0,000001, до 1 млрд, не более 6 знаков после запятой.");
            e.Cancel = true;
            return;
        }
        if (qty == row.RecordedQty) return;
        var revision = ServerRevision();
        var allowOver = ShipmentQuantityPolicy.ExceedsRemaining(row.Remaining, qty);
        // Reserve the operation synchronously, then let WPF finish the edit transaction.
        await WriteQuantityAsync(() => SqlServerShipments.SetQty(
            _connectionString, current.Id, row.PositionId, qty, allowOver, revision, current.Fixed),
            "Количество сохранено.",
            confirm: () => !allowOver || Ask("Больше, чем по смете",
                $"По смете осталось выдать {Num(row.Remaining)}.\nВыдать {Num(qty)} — больше сметы?"),
            defer: true, changedPositions: new[] { row.PositionId });
    }

    private sealed record OperationOutcome(string Message, bool Reload = true);

    private Task WriteQuantityAsync(Func<SqlServerDatabase.SaveResult> write, string success,
        Func<bool>? confirm = null, bool defer = false, IReadOnlyList<long>? changedPositions = null) =>
        RunOperationAsync(async () =>
        {
            if (confirm is not null && !confirm()) return new OperationOutcome("Операция отменена.");
            if (!IsLoaded || _needsReloadAfterAttach) return new OperationOutcome("Раздел закрыт. Запись не начата.");
            var saved = await Task.Run(write);
            if (!saved.Saved) return new OperationOutcome("Ошибка: " + saved.Error);
            if (changedPositions is not null)
            {
                // Never retry a committed write just because a read or UI patch failed.
                try
                {
                    if (await TryApplyChangedRowsAsync(changedPositions, saved.Revision))
                        return new OperationOutcome(success, Reload: false);
                }
                catch (Exception) { /* safely reload the whole committed shipment below */ }
            }
            return new OperationOutcome(success);
        }, defer);

    private async Task RunOperationAsync(Func<Task<OperationOutcome>> operation, bool defer = false)
    {
        if (!CanStartWrite) return;
        _quantityBusy = true;
        var attachment = _attachmentGeneration;
        var reload = true;
        try
        {
            if (defer)
            {
                await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
                if (!IsLoaded || attachment != _attachmentGeneration) return;
                if (!LinesGrid.CommitEdit(DataGridEditingUnit.Row, true))
                {
                    reload = false;
                    SetStatus("Завершите редактирование строки. В SQL ничего не записано.");
                    return;
                }
            }
            if (!IsLoaded || attachment != _attachmentGeneration) return;
            IsEnabled = false;
            SetStatus("Сохранение…");
            var outcome = await operation();
            reload = outcome.Reload;
            SetStatus(outcome.Message);
        }
        catch (Exception ex) { SetStatus("Ошибка: " + ex.Message); }
        finally
        {
            try { if (IsLoaded && (reload || _needsReloadAfterAttach)) await ReloadAsync(preserveStatus: true); }
            catch (Exception ex) { SetStatus("Не удалось перечитать ведомость: " + ex.Message); }
            finally
            {
                _building = false;
                _quantityBusy = false;
                IsEnabled = true;
                // A reattach during the mandatory readback could not start a
                // clean read while the operation owned the pane.
                if (attachment != _attachmentGeneration) ResumeShipmentReadIfNeeded();
            }
        }
    }

    private void SetStatus(string message)
    {
        if (IsLoaded) FooterText.Text = message;
    }
    /// <summary>Export the selected shipment exactly as recorded, not the grid's pending edits.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !RequireSavedHeader()) return;
        var current = Current;
        if (current is null) return;
        var revision = _revision;
        var name = "Ведомость № " + current.Number + " — " +
            CaptionText.Text.Replace("Выдача на объект: ", "");
        var saved = await ContractDocumentSave.XlsxAsync(Window.GetWindow(this), name, () =>
        {
            using var conn = SqlServerDatabase.Open(_connectionString);
            var snapshot = SqlServerShipmentPrintData.Read(conn, _objectId, current.Id, revision);
            return ShipmentExcelForms.Create(snapshot);
        });
        if (saved) MarkShipmentExported(current.Id);
    }

    private async void ExportPdf_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartWrite || !RequireSavedHeader()) return;
        var current = Current;
        if (current is null) return;
        var revision = _revision;
        var name = "Ведомость № " + current.Number + " — " +
            CaptionText.Text.Replace("Выдача на объект: ", "");
        var saved = await ContractDocumentSave.PdfAsync(Window.GetWindow(this), name, () =>
        {
            using var conn = SqlServerDatabase.Open(_connectionString);
            return ShipmentPdfForms.Create(SqlServerShipmentPrintData.Read(conn, _objectId, current.Id, revision));
        });
        if (saved) MarkShipmentExported(current.Id);
    }

}
