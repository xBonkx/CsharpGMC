using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>Строка окна «Закупки» — отображение строки закупки.</summary>
public sealed class ProcurementPreviewRow
{
    public long PositionId { get; init; }
    public long SystemId { get; init; }
    public long? ParentSystemId { get; init; }
    public string SystemName { get; init; } = "";
    public string Source { get; init; } = "buy";
    public string StatusLabel { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Price { get; init; } = "";
    public string Received { get; init; } = "";
    public string Issued { get; init; } = "";
    public string Supplier { get; set; } = "";
    public string Stage { get; set; } = "";
    public string StageLabel
    {
        get
        {
            var label = SqlServerProcurement.StatusLabel(Stage);
            return string.IsNullOrWhiteSpace(label) ? Stage : label;
        }
    }
    public string StatusCode { get; init; } = "";
    public string OrderRef { get; set; } = "";
    public string InvoiceRef { get; set; } = "";
    public string CostPrice { get; set; } = "";
    public string DueDate { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string Note { get; set; } = "";
    public string Flags { get; init; } = "";
    public IReadOnlyList<string> FlagItems { get; init; } = Array.Empty<string>();
    public string RowBackground { get; init; } = "White";
    public bool Work { get; init; }
    public bool OutOfKp { get; init; }
    public string BuyName { get; init; } = "";
    public decimal QuantityValue { get; init; }
    public decimal? CostValue { get; init; }
    public DateTime? LastIssueDate { get; init; }
    public int OriginalIndex { get; init; }
    public string GroupLabel { get; set; } = "";
    public string GroupTitle { get; set; } = "";
    public string GroupNumber { get; set; } = "";
    public string GroupDisplayNumber { get; set; } = "";
    public string GroupIdentity { get; set; } = "";
    public string GroupBackground => ParentSystemId is not null &&
        GroupIdentity.StartsWith("system:", StringComparison.Ordinal) ? "#E8F5FC" : "#F1F4F9";
    public string EstimateName { get; init; } = "";
    public string NameTooltip => (BuyName == EstimateName ? BuyName : BuyName + "\nВ смете: " + EstimateName) + (Flags.Length > 0 ? "\n⚠ " + Flags : "");
    public bool HasProblem => FlagItems.Count > 0;
    public bool HasBuyName => BuyName != EstimateName;
    public decimal ReceivedValue { get; init; }
    public decimal IssuedValue { get; init; }
    public DateTime? DueOn { get; init; }
    public string ExpectedCaption => ProcurementDisplay.ExpectedCaption(DueOn, DateTime.Today);
    public string ExpectedState => ProcurementDisplay.ExpectedState(DueOn, StatusCode, DateTime.Today);
    public ProcurementBadgeLook StatusBadge => ProcurementBadgePalette.Status(StatusCode);
    public ProcurementBadgeLook DueBadge => ProcurementBadgePalette.Due(ExpectedState);
    public string DueTooltip => ProcurementDisplay.ExpectedTooltip(DueOn, StatusCode, DateTime.Today);
    public string RowNumber { get; set; } = "";
    public bool IsSub { get; init; }
    public string StageChoice => string.IsNullOrEmpty(Stage) || Stage == "plan" ? "none" : Stage;
    public string RemainingText { get; init; } = "";
    public string IssueDateText => LastIssueDate?.ToString("dd.MM.yyyy") ?? "";
    public DateTime? PaidDate { get; init; }
    public string Responsible { get; init; } = "";
    public bool DirectToSite { get; init; }
    public string OrderCaption => ProcurementDisplay.OrderCaption(OrderRef, InvoiceRef, PaidDate);
    public string NoteCaption => ProcurementDisplay.NoteCaption(Note, Replacement);
    public string MarginText { get; init; } = "";
}

public sealed class ProcurementSystemFilterChoice
{
    public long Id { get; init; }
    public string Label { get; init; } = "";
    public bool IsSelected { get; set; }
}

public sealed record ProcurementSupplierFilterChoice(string Label, string? Key);

/// <summary>
/// «Закупки — контроль оборудования» (proc_view.py, ядро procurement.py): по строкам
/// КП — производный статус, поставщик, этап (заказано/счёт/оплачено/по факту…),
/// заявка/счёт, закупочная цена, срок, замена, примечание, пришло/выдано.
/// Движения (пришло/выдано/возвраты) — следующий блок.
/// </summary>
public partial class ProcurementPane : UserControl
{
    private readonly string _connectionString;
    private readonly int _contractId;
    private string _revision = "";
    // Mutable WPF bindings can change a row even before an edit is committed.
    // Only a full authoritative publication clears this conservative guard.
    private bool _buySnapshotMayReuse;
    private List<ProcurementPreviewRow> _allRows = new();
    private string _activeCategory = "all";
    private readonly HashSet<long> _selectedSystemIds = new();
    private bool _rebuildingScopeFilters;
    private bool _batchBusy;
    private int _historyLoadToken;
    private int _reloadGeneration;
    private int _attachmentGeneration;
    private bool _needsReloadAfterAttach = true;
    private string _historyRevision = "";
    private List<SqlServerProcurement.SystemChoice> _allSystems = new();
    private List<ProcurementSystemFilterChoice> _systemChoices = new();
    private bool _openOnly, _missingCost, _showMoney;
    private string _viewMode = "kp";
    private string _viewSort = "kp";
    public sealed record ViewChoice(string Code, string Label);
    public IReadOnlyList<ViewChoice> ViewModes { get; } =
        ProcurementViewPlanner.Modes.Select(m => new ViewChoice(m.Code, m.Label)).ToArray();
    public IReadOnlyList<ViewChoice> ViewSorts { get; } =
        ProcurementViewPlanner.Sorts.Select(m => new ViewChoice(m.Code, m.Label)).ToArray();

    public sealed record StageOption(string Code, string Label);
    public IReadOnlyList<StageOption> StageOptions { get; } =
        new[]
        {
            new StageOption("none", "Не заказано"),
            new StageOption("ordered", "Заказано"),
            new StageOption("shop_queued", "Заявка в цех"),
            new StageOption("shop_work", "В изготовлении (цех)"),
            new StageOption("invoice", "Счёт получен"),
            new StageOption("paid", "Оплачено"),
            new StageOption("fact", "По факту / монтажники"),
            new StageOption("skip", "Не заказываем"),
        };

    public ProcurementPane(string connectionString, int contractId, string title)
    {
        _connectionString = connectionString;
        _contractId = contractId;
        InitializeComponent();
        BuyGrid.PreviewKeyDown += BuyGrid_OrderShortcut;

        Loaded += (_, _) => ResumeVisibleBuyReads();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) ResumeVisibleBuyReads();
            else SuspendBuyReads();
        };
        Unloaded += (_, _) =>
        {
            ++_attachmentGeneration;
            ++_reloadGeneration;
            SuspendBuyReads();
            ++_buyViewportGeneration;
            _revision = _historyRevision = "";
            _buySnapshotMayReuse = false;
            _needsReloadAfterAttach = true;
        };
    }

    // Only the revision of the successfully applied screen snapshot can authorize a write.
    private bool CanStartOperation => IsLoaded && !_batchBusy && !string.IsNullOrEmpty(_revision);
    internal bool HasCacheActivity => _batchBusy ||
        (BuyGrid.ItemsSource is System.ComponentModel.IEditableCollectionView { IsEditingItem: true });

    private sealed class OperationLease : IDisposable
    {
        private readonly ProcurementPane _owner;
        private readonly int _attachment;
        public string Revision { get; }
        public OperationLease(ProcurementPane owner, string revision)
        {
            _owner = owner;
            _attachment = owner._attachmentGeneration;
            Revision = revision;
        }
        public string BeginWrite()
        {
            if (!_owner.IsLoaded || _attachment != _owner._attachmentGeneration)
                throw new InvalidOperationException("Раздел закрыт или открыт заново. Запись не начата.");
            // Never attach a returned/new revision to an old grid, even on failure.
            _owner._revision = "";
            _owner.CancelMovementHistory();
            return Revision;
        }
        public void Dispose()
        {
            _owner._batchBusy = false;
            _owner.IsEnabled = true;
            if (!_owner.IsLoaded) return;
            _owner.UpdateHistoryButtons();
            if (_owner._needsReloadAfterAttach && _attachment != _owner._attachmentGeneration)
                _ = _owner.TryReloadAsync();
        }
    }

    private OperationLease? TryBeginOperation(bool deferDisable = false)
    {
        if (!CanStartOperation)
        {
            if (!_batchBusy) SetStatus("Данные закупок не подтверждены. Нажмите «Обновить».");
            return null;
        }
        // Reserve synchronously, before a modal dialog or any await.
        _batchBusy = true;
        var lease = new OperationLease(this, _revision);
        if (!deferDisable) IsEnabled = false;
        return lease;
    }

    private async Task<bool> CheckPreviewRevisionAsync(OperationLease operation, string revision)
    {
        if (revision == operation.Revision && revision.Length != 0) return true;
        _revision = "";
        SetStatus("Закупки изменены другим сотрудником. Данные перечитываются; повторите действие.");
        await TryReloadAsync(preserveStatus: true);
        return false;
    }

    private static ProcurementPreviewRow BuildBuyPreview(SqlServerProcurement.BuyRow row,
        string rowNumber, string groupNumber, int originalIndex)
    {
        string Q(decimal v) => ProcurementDisplay.Quantity(v);
        var buyName = string.IsNullOrWhiteSpace(row.PurchaseName) ? row.Name : row.PurchaseName;
        return new ProcurementPreviewRow
        {
            PositionId = row.PositionId,
            SystemId = row.SystemId,
            ParentSystemId = row.ParentSystemId,
            SystemName = row.SystemName,
            Source = row.Source,
            StatusLabel = row.StatusLabel,
            StatusCode = row.Status,
            Name = (row.OutOfKp ? "«вне КП» " : "") + buyName,
            RowNumber = rowNumber,
            GroupNumber = groupNumber,
            IsSub = row.ParentPositionId is not null,
            PaidDate = row.PaidDate,
            EstimateName = row.Name,
            ReceivedValue = row.Received,
            IssuedValue = row.Issued,
            DueOn = row.DueDate,
            Responsible = row.Responsible,
            DirectToSite = row.DirectToSite,
            RemainingText = ProcurementDisplay.Remaining(row.Qty, row.Issued, row.Status, CultureInfo.GetCultureInfo("ru-RU")),
            MarginText = row.CostPrice is decimal cost && row.ParentPositionId is null && !row.OutOfKp && row.Price != 0m ? Money.Format((row.Price - cost) * row.Qty) : "",
            Unit = row.Unit,
            Qty = Q(row.Qty),
            Price = row.Price == 0m ? "—" : Money.Format(row.Price),
            Received = row.Received == 0m ? "" : Q(row.Received),
            Issued = row.Issued == 0m ? "" : Q(row.Issued),
            Supplier = row.Supplier ?? "",
            Stage = row.Stage,
            OrderRef = row.OrderRef,
            InvoiceRef = row.InvoiceRef,
            CostPrice = row.CostPrice is null ? "" : Num(row.CostPrice.Value),
            DueDate = row.DueDate is null ? "" : row.DueDate.Value.ToString("dd.MM.yyyy"),
            Replacement = row.Replacement,
            Note = row.Note,
            Flags = string.Join("; ", row.Flags),
            FlagItems = row.Flags,
            RowBackground = row.StatusBg,
            Work = row.Work,
            OutOfKp = row.OutOfKp,
            BuyName = buyName,
            QuantityValue = row.Qty,
            CostValue = row.CostPrice,
            LastIssueDate = row.LastIssueDate,
            OriginalIndex = originalIndex,
        };
    }

    private void RebuildScopeFilters()
    {
        // Python proc_view.py:load keeps the selected supplier and selected
        // system IDs across reload; choices come only from this contract.
        var previouslySelectedSupplier =
            (ProcurementSupplierFilter.SelectedItem as ProcurementSupplierFilterChoice)?.Key;
        _rebuildingScopeFilters = true;
        try
        {
            // Include empty parent systems: they are selectable in Python's SystemPicker.
            var systems = _allSystems.OrderBy(x => x.Order).ThenBy(x => x.Id).ToList();
            _selectedSystemIds.IntersectWith(systems.Select(row => row.Id));
            var duplicateNames = systems
                .GroupBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _systemChoices = systems.Select(row => new ProcurementSystemFilterChoice
                {
                    Id = row.Id,
                    Label = duplicateNames.Contains(row.Name)
                        ? $"{row.Name} · #{row.Id}" : row.Name,
                    IsSelected = _selectedSystemIds.Contains(row.Id),
                }).ToList();
            RebuildVisibleSystemChoices();

            var suppliers = _allRows.Select(row => row.Supplier.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            var choices = new List<ProcurementSupplierFilterChoice>
            {
                new("Все поставщики", null),
                new("(не указан)", ""),
            };
            choices.AddRange(suppliers.Select(name => new ProcurementSupplierFilterChoice(name, name)));
            ProcurementSupplierFilter.ItemsSource = choices;
            ProcurementSupplierFilter.SelectedItem = choices.FirstOrDefault(choice =>
                string.Equals(choice.Key, previouslySelectedSupplier, StringComparison.Ordinal))
                ?? choices[0];
        }
        finally
        {
            _rebuildingScopeFilters = false;
        }
        UpdateSystemFilterTitle();
    }

    private void RebuildVisibleSystemChoices()
    {
        if (ProcurementSystemItems is null) return;
        var query = ProcurementSystemSearch?.Text.Trim() ?? "";
        ProcurementSystemItems.ItemsSource = _systemChoices
            .Where(choice => query.Length == 0 ||
                choice.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(choice => new ProcurementSystemFilterChoice
            {
                Id = choice.Id, Label = choice.Label,
                IsSelected = _selectedSystemIds.Contains(choice.Id),
            }).ToList();
    }

    private void ProcurementSystemSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _rebuildingScopeFilters = true;
        try { RebuildVisibleSystemChoices(); }
        finally { _rebuildingScopeFilters = false; }
    }

    private void UpdateSystemFilterTitle()
    {
        var selected = _systemChoices.Where(choice => _selectedSystemIds.Contains(choice.Id))
            .Select(choice => choice.Label).ToList();
        ProcurementSystemToggle.Content = selected.Count switch
        {
            0 => "Все системы",
            1 => selected[0],
            2 => string.Join(", ", selected),
            _ => $"Систем: {selected.Count}",
        };
        ProcurementSystemToggle.Tag = selected.Count > 0 ? "filtered" : null;
        ProcurementSystemToggle.ToolTip = selected.Count == 0
            ? "Показать только выбранные системы (можно несколько)"
            : string.Join(", ", selected);
    }

    private void ProcurementSystem_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (_rebuildingScopeFilters || sender is not CheckBox { Tag: long id } box)
            return;
        if (box.IsChecked == true)
            _selectedSystemIds.Add(id);
        else
            _selectedSystemIds.Remove(id);
        UpdateSystemFilterTitle();
        ApplyProcurementFilters();
    }

    private void ProcurementSystemsClear_Click(object sender, RoutedEventArgs e)
    {
        _selectedSystemIds.Clear();
        _rebuildingScopeFilters = true;
        try { RebuildVisibleSystemChoices(); }
        finally { _rebuildingScopeFilters = false; }
        UpdateSystemFilterTitle();
        ApplyProcurementFilters();
    }

    private void ProcurementSupplier_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_rebuildingScopeFilters) ApplyProcurementFilters();
    }

    private void UpdateCategoryCounts()
    {
        // Python proc_view.py:_show_chips lists counts. These reflect the
        // loaded SQL rows and remain stable when another filter is selected.
        static void SetChip(RadioButton button, string label, int count) =>
            button.Content = count > 0 ? $"{label} · {count}" : label;

        var counts = ProcurementViewFilter.CountCategories(_allRows.Select(row =>
            (row.StatusCode, row.Work, (IReadOnlyList<string>)row.FlagItems, row.Source)));
        SetChip(ProcFilterAll, "Все", counts.All);
        SetChip(ProcFilterNone, "Не заказано", counts.None);
        SetChip(ProcFilterWork, "В работе", counts.Work);
        SetChip(ProcFilterLate, "Просрочено", counts.Late);
        SetChip(ProcFilterShop, "Цех", counts.Shop);
        SetChip(ProcFilterStock, "На складе", counts.Stock);
        ProcFilterIssued.Content = $"Выдано · {counts.Issued} из {counts.All}";
        SetChip(ProcFilterProblem, "Проблемы", counts.Problem);
        ProcFilterLate.Visibility = counts.Late > 0 || _activeCategory == "late" ? Visibility.Visible : Visibility.Collapsed;
        ProcFilterShop.Visibility = counts.Shop > 0 || _activeCategory == "shop" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ProcurementCategory_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string category }) return;
        _activeCategory = category;
        ApplyProcurementFilters();
    }

    private void ApplyProcurementFilters()
    {
        if (BuyGrid is null || ProcurementSearchBox is null || ProcurementSupplierFilter is null) return;
        if (BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true }) return;
        if (_rebuildingScopeFilters) return;

        var q = ProcurementSearchBox.Text.Trim();
        var supplierChoice =
            (ProcurementSupplierFilter.SelectedItem as ProcurementSupplierFilterChoice)?.Key;
        IEnumerable<ProcurementPreviewRow> rows = _allRows.Where(row =>
            ProcurementViewFilter.Matches(_activeCategory, row.StatusCode,
                row.Stage, row.Work, row.FlagItems, row.Source) &&
            ProcurementScopeFilter.Matches(row.SystemId, row.ParentSystemId,
                row.SystemName, row.Supplier, _selectedSystemIds, supplierChoice,
                q, row.Name, row.OrderRef, row.InvoiceRef, row.Replacement));
        if (_openOnly)
            rows = rows.Where(r => r.StatusCode is not ("zero" or "skip" or "issued"));
        if (_missingCost)
            rows = rows.Where(r => !r.Work && string.IsNullOrWhiteSpace(r.CostPrice));

        var visible = rows.ToList();
        var plan = ProcurementViewPlanner.Build(
            visible.Select(row => new ProcurementViewSource(
                row.PositionId, row.SystemId, row.SystemName, row.BuyName,
                row.Unit, row.Supplier, row.StatusCode, row.QuantityValue,
                row.CostValue, row.LastIssueDate, row.OriginalIndex)).ToList(),
            _viewMode, _viewSort);
        var byId = visible.ToDictionary(row => row.PositionId);
        var display = plan.Select(entry =>
        {
            var row = byId[entry.PositionId];
            row.GroupLabel = entry.GroupLabel;
            row.GroupTitle = _viewMode == "kp" ? row.SystemName : entry.GroupLabel;
            row.GroupDisplayNumber = _viewMode == "kp" && _viewSort == "kp" ? row.GroupNumber : "";
            row.GroupIdentity = _viewMode switch
            {
                "kp" => "system:" + row.SystemId,
                "same" => "same:" + row.BuyName.Trim().ToUpperInvariant() + "\u001f" + row.Unit.Trim().ToUpperInvariant(),
                _ => "supplier:" + row.Supplier.Trim().ToUpperInvariant()
            };
            return row;
        }).ToList();
        ApplyBuyDisplay(display, !(_viewMode == "kp" && _viewSort != "kp"));
    }

    private void ProcurementSelectGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CollectionViewGroup group }) return;
        BuyGrid.SelectedItems.Clear();
        foreach (var row in group.Items.OfType<ProcurementPreviewRow>())
            BuyGrid.SelectedItems.Add(row);
        SetStatus("");
    }

    private void BuyGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringBuySelection || ProcurementSelectionText is null) return;
        var count = BuyGrid.SelectedItems.Count;
        ProcurementSelectionText.Text = count > 0 ? $"Выделено: {count} →" : "Выделите строки →";
        ProcurementSelectionText.Foreground = (Brush)new BrushConverter().ConvertFromString(count > 0 ? "#1D4ED8" : "#64748B")!;
        if (ProcurementSupplierButton is not null) ProcurementSupplierButton.IsEnabled = count > 0;
        if (ProcurementStatusButton is not null) ProcurementStatusButton.IsEnabled = count > 0;
        // Loading is lazy: merely selecting cells does not query the network
        // while the Python-style history panel is collapsed.
        CancelMovementHistory();
        if (MovementEditButton is not null) MovementEditButton.IsEnabled = false;
        if (MovementDeleteButton is not null) MovementDeleteButton.IsEnabled = false;
        if (MovementHistoryGrid is not null) MovementHistoryGrid.ItemsSource = null;
        var row = SelectedHistoryRow();
        if (MovementHistoryExpander is not null)
            MovementHistoryExpander.Header = row is null
                ? "Движение и замена — выберите одну позицию"
                : $"Движение и замена: {row.BuyName[..Math.Min(65, row.BuyName.Length)]}";
        if (MovementDetailsText is not null)
            MovementDetailsText.Text = row is null
                ? "Выберите одну строку закупки для просмотра истории."
                : $"По КП: {row.Qty} {row.Unit} · пришло {row.Received} · выдано {row.Issued}";
        if (ReplacementDetail is not null)
        {
            ReplacementDetail.Text = row?.Replacement ?? "";
            ResponsibleDetail.Text = row?.Responsible ?? "";
            DirectDetail.IsChecked = row?.DirectToSite ?? false;
            ReplacementDetail.IsEnabled = ResponsibleDetail.IsEnabled = DirectDetail.IsEnabled = SaveDetailButton.IsEnabled = row is not null;
        }
        if (MovementHistoryExpander?.IsExpanded == true && !_applyingBuySnapshot)
            _ = LoadSelectedMovementHistoryAsync();
    }

    private ProcurementPreviewRow? SelectedHistoryRow()
    {
        if (BuyGrid is null || BuyGrid.SelectedItems.Count != 1) return null;
        return BuyGrid.SelectedItem as ProcurementPreviewRow;
    }

    private void MovementHistory_Expanded(object sender, RoutedEventArgs e) =>
        _ = LoadSelectedMovementHistoryAsync();

    private void MovementHistory_Collapsed(object sender, RoutedEventArgs e)
    {
        CancelMovementHistory();
        _historyNeedsResume = false;
        UpdateHistoryButtons();
    }

    private void MovementHistory_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateHistoryButtons();

    private void UpdateHistoryButtons()
    {
        if (MovementDeleteButton is null || MovementEditButton is null || MovementHistoryGrid is null) return;
        MovementDeleteButton.IsEnabled = CanStartOperation && _historyRevision == _revision &&
            MovementHistoryGrid.SelectedItem is SqlServerProcurement.MovementHistoryRow movement &&
            SelectedHistoryRow() is ProcurementPreviewRow row &&
            SqlServerProcurement.MovementDeletionError(movement, row.PositionId) is null;
        MovementEditButton.IsEnabled = MovementDeleteButton.IsEnabled;
    }

    private async void MovementDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation || _historyRevision != _revision || SelectedHistoryRow() is not ProcurementPreviewRow row ||
            MovementHistoryGrid.SelectedItem is not SqlServerProcurement.MovementHistoryRow movement)
            return;
        var error = SqlServerProcurement.MovementDeletionError(movement, row.PositionId);
        if (error is not null) { SetStatus(error); return; }
        using var operation = TryBeginOperation();
        if (operation is null) return;
        if (MessageBox.Show(Window.GetWindow(this),
                $"Удалить запись «{movement.KindLabel}» на {movement.Qty:0.###} {row.Unit}?" +
                "\nЭто изменит приход/выдачу и производный статус позиции.",
                "Удаление движения", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            != MessageBoxResult.Yes) return;

        MovementDeleteButton.IsEnabled = false;
        SetStatus("Удаление движения…");
        var positionId = row.PositionId;
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.DeleteMovement(
                _connectionString, _contractId, positionId, movement.Id, revision));

            if (!result.Saved)
            {
                SetStatus("Не удалено: " + result.Error);
                await TryReloadAsync(preserveStatus: true);
                return;
            }
            await RefreshCommittedBuyRowsAsync(new[] { positionId }, result.Revision);
            SetStatus("Движение удалено; остатки перечитаны из SQL Server.");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Результат удаления не подтверждён: " + ex.Message + ". Проверьте историю перед повтором.");
            await TryReloadAsync(preserveStatus: true);
        }
        finally { UpdateHistoryButtons(); }
    }

    private IReadOnlyList<long> SelectedBuyingIds()
    {
        // A synthetic group heading is never sent to SQL. Use only real IDs
        // already in this contract's current server snapshot.
        var valid = _allRows.Where(row => !row.Work)
            .Select(row => row.PositionId).ToHashSet();
        return BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>()
            .Select(row => row.PositionId).Where(valid.Contains)
            .Distinct().ToArray();
    }

    private async Task ApplyBulkProcAsync(string? supplier, string? stage, string description, DateTime? due = null, bool clearDue = false)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;
        var ids = SelectedBuyingIds();
        if (ids.Count == 0)
        {
            SetStatus("Выделите строки или нажмите «Выделить группу».");
            return;
        }

        BuyGrid.IsReadOnly = true;
        SetStatus($"Сохранение {ids.Count} позиций…");
        string message;
        string? committedRevision = null;
        try
        {
            var revision = operation.BeginWrite();
            var result = await Task.Run(() => SqlServerProcurement.SetProc(
                _connectionString, _contractId, ids, supplier, stage,
                null, null, null, due, null, null, null, revision, clearDueDate: clearDue));

            if (result.Saved) committedRevision = result.Revision;
            message = result.Saved
                ? $"{description} — {ids.Count} позиций сохранено одной SQL-транзакцией."
                : "Изменения не сохранены: " + result.Error;
        }
        catch (Exception ex)
        {
            _revision = "";
            message = "Ошибка пакетной операции: " + ex.Message;
        }
        finally { BuyGrid.IsReadOnly = false; }
        try
        {
            if (committedRevision is not null) await RefreshCommittedBuyRowsAsync(ids, committedRevision);
            else await ReloadAsync(preserveStatus: true);
        }
        catch (Exception ex) { message += " · Ошибка перечитывания: " + ex.Message; }
        SetStatus(message);
    }

    private async void ProcurementBulkSupplier_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartOperation) return;
        var current = BuyGrid.SelectedItems.OfType<ProcurementPreviewRow>()
            .Select(x => x.Supplier).Distinct(StringComparer.Ordinal).ToList();
        var initial = current.Count == 1 ? current[0] : "";
        var dialog = new PromptDialog("Поставщик для выделенных",
            "Поставщик для выделенных строк (пустое значение очищает):",
            initial) { Owner = Window.GetWindow(this) };
        var revision = _revision;
        var ids = SelectedBuyingIds().ToArray();
        if (dialog.ShowDialog() != true) return;
        if (_revision != revision || !SelectedBuyingIds().SequenceEqual(ids))
        {
            SetStatus("Выбор или данные изменились. Повторите назначение поставщика.");
            return;
        }
        await ApplyBulkProcAsync(dialog.Value.Trim(), null, "Поставщик обновлён");
    }

    private void ProcurementFilter_Changed(object sender, RoutedEventArgs e) =>
        ApplyProcurementFilters();

    public void RefreshFromSqlServer()
    {
        if (IsLoaded && !_batchBusy && BuyGrid.ItemsSource is not ListCollectionView { IsEditingItem: true })
            _ = TryReloadAsync(requiredReadback: false);
    }

    internal void RefreshOnTabReturn()
    {
        if (IsLoaded && !_batchBusy && _ordinaryReloadCancellation is null &&
            BuyGrid.ItemsSource is not ListCollectionView { IsEditingItem: true })
            _ = TryReloadAsync(allowUnchanged: true, requiredReadback: false);
    }

    private async void ProcurementRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_batchBusy || BuyGrid.ItemsSource is ListCollectionView { IsEditingItem: true }) return;
        try
        {
            if (await ReloadAsync(requiredReadback: false))
                SetStatus("Закупки обновлены из SQL Server.");
        }
        catch (Exception ex) { SetStatus("Ошибка обновления закупок: " + ex.Message); }
    }

    private static string Num(decimal v) => v.ToString("0.######", CultureInfo.CurrentCulture);

    private static string? Field(DataGridColumn column) => column.Header?.ToString() switch
    {
        "Поставщик" => "supplier",
        "Статус ▾" => "stage",
        "Заявка / счёт" => "order_ref",
        "Наша цена" => "cost_price",
        "Ожидаем" => "due_date",
        "Примечание / замена" => "note",
        _ => null,
    };

    private sealed record BuyEditSnapshot(long PositionId, string Field, string Text, decimal? Cost, DateTime? Due);
    private BuyEditSnapshot? _buyEditSnapshot;

    private void BuyGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        _buyEditSnapshot = null;
        if (!CanStartOperation || e.Row.Item is not ProcurementPreviewRow row) { e.Cancel = true; return; }
        var field = Field(e.Column);
        if (field is null || row.Work && field != "supplier") { e.Cancel = true; return; }
        _buySnapshotMayReuse = false;
        var original = field switch
        {
            "supplier" => row.Supplier,
            "stage" => row.StageChoice,
            "order_ref" => row.OrderRef,
            "note" => row.Note,
            _ => ""
        };
        _buyEditSnapshot = new BuyEditSnapshot(row.PositionId, field, original.Trim(), row.CostValue, row.DueOn?.Date);
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T matched) return matched;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var match = FindVisualChild<T>(VisualTreeHelper.GetChild(root, i));
            if (match is not null) return match;
        }
        return null;
    }

    private async void BuyGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit ||
            e.Row.Item is not ProcurementPreviewRow row) return;
        var field = Field(e.Column);
        if (field is null) return;
        if (!CanStartOperation) { e.Cancel = true; return; }

        // DataGridTemplateColumn uses ComboBox for stage codes, while other
        // editable columns use text. The SQL backend accepts stable codes.
        var input = e.EditingElement switch
        {
            TextBox box => box.Text.Trim(),
            ComboBox choice => choice.SelectedValue?.ToString()?.Trim(),
            ContentPresenter presenter => FindVisualChild<ComboBox>(presenter)?.SelectedValue?.ToString()?.Trim()
                ?? FindVisualChild<TextBox>(presenter)?.Text.Trim(),
            _ => null,
        };
        if (input is null) return;

        decimal? cost = null;
        DateTime? due = null;
        bool clearCost = field == "cost_price" && input.Length == 0;
        bool clearDue = field == "due_date" && input.Length == 0;
        if (field == "cost_price" && !clearCost)
        {
            if (!ProcurementDisplay.TryParseNumber(input, out var parsedCost))
            {
                SetStatus("Недопустимая закупочная цена: " + input);
                e.Cancel = true;
                return;
            }
            cost = parsedCost;
        }
        if (field == "due_date" && !clearDue)
        {
            if (!ProcurementDisplay.TryParseDue(input, DateTime.Today, out due))
            {
                SetStatus("Укажите дату: 25.11, 25.11.2026 или +7");
                e.Cancel = true;
                return;
            }
        }
        if (field == "stage" && !StageOptions.Any(s => s.Code == input))
        {
            SetStatus("Неизвестный этап закупки: " + input);
            e.Cancel = true;
            return;
        }

        // The grid editor may have updated its TwoWay binding already.
        // Compare the normalized input against the captured original, not that mutable row.
        if (_buyEditSnapshot is { } original && original.PositionId == row.PositionId &&
            original.Field == field)
        {
            var unchanged = field switch
            {
                "cost_price" => cost == original.Cost,
                "due_date" => due?.Date == original.Due,
                _ => string.Equals(input, original.Text, StringComparison.Ordinal)
            };
            if (unchanged) { _buyEditSnapshot = null; return; }
        }
        _buyEditSnapshot = null;
        using var operation = TryBeginOperation(deferDisable: true);
        if (operation is null) { e.Cancel = true; return; }
        SetStatus("Сохранение…");
        string resultMessage;
        string? committedRevision = null;
        try
        {
            // Finish the WPF edit before disabling/replacing its grid.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            if (!BuyGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                SetStatus("Завершите редактирование строки. В SQL ничего не записано.");
                return;
            }
            IsEnabled = false;
            var revision = operation.BeginWrite();
            var saved = await Task.Run(() => SqlServerProcurement.SetProc(
                _connectionString, _contractId, new[] { row.PositionId },
                field == "supplier" ? input : null,
                field == "stage" ? (input == "none" ? "" : input) :
                    field == "order_ref" && input.Length > 0 && (row.Stage is "" or "skip" or "plan") ? "ordered" : null,
                field == "order_ref" ? input : null,
                field == "invoice_ref" ? input : null,
                field == "cost_price" ? cost : null,
                field == "due_date" ? due : null,
                field == "replacement" ? input : null,
                field == "note" ? input : null,
                null, revision,
                clearCostPrice: clearCost, clearDueDate: clearDue));

            if (saved.Saved) committedRevision = saved.Revision;
            resultMessage = saved.Saved
                ? "Сохранено в SQL Server."
                : "Не сохранено: " + saved.Error;
        }
        catch (Exception ex)
        {
            _revision = "";
            resultMessage = "Ошибка сохранения: " + ex.Message;
        }

        try
        {
            if (committedRevision is not null)
                await RefreshCommittedBuyRowsAsync(new[] { row.PositionId }, committedRevision);
            else await ReloadAsync();
        }
        catch (Exception ex)
        {
            resultMessage += " · Ошибка обновления: " + ex.Message;
        }
        SetStatus(resultMessage);
    }

    private void BuyGrid_OrderShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F4 ||
            System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None ||
            e.OriginalSource is TextBox || e.OriginalSource is ComboBox) return;
        if (!CanStartOperation || SelectedBuyingIds().Count == 0) return;
        e.Handled = true;
        if (!BuyGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !BuyGrid.CommitEdit(DataGridEditingUnit.Row, true) || !CanStartOperation)
        {
            SetStatus("Завершите сохранение ячейки и повторите F4.");
            return;
        }
        ProcurementOrder_Click(sender, e);
    }

    private void BuyGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MovementMenu.Items.Clear();
        if (!CanStartOperation)
        {
            e.Handled = true;
            return;
        }
        var row = ContextMenuTarget.SelectRow<ProcurementPreviewRow>(BuyGrid, e);
        if (row is null || row.Work || !_allRows.Contains(row))
        {
            e.Handled = true;
            return;
        }
        var revision = _revision;
        var ids = SelectedBuyingIds().OrderBy(id => id).ToArray();
        bool CanExecute() => CanStartOperation && _revision == revision &&
            _allRows.Contains(row) && SelectedBuyingIds().OrderBy(id => id).SequenceEqual(ids);
        void Add(string header, string kind, string? glyph = null, string defaultDocument = "")
        {
            var item = glyph is null ? new MenuItem { Header = header } : ContextMenuIcons.Create(header, glyph);
            item.Click += (_, _) =>
            {
                if (CanExecute()) _ = OpenMovementDialogAsync(row, kind, defaultDocument);
            };
            MovementMenu.Items.Add(item);
        }
        void AddAction(string header, RoutedEventHandler handler, string? glyph = null, string shortcut = "")
        {
            var item = glyph is null ? new MenuItem { Header = header, InputGestureText = shortcut } :
                ContextMenuIcons.Create(header, glyph, shortcut: shortcut);
            item.Click += (sender, args) => { if (CanExecute()) handler(sender, args); };
            MovementMenu.Items.Add(item);
        }
        AddAction("Поставщик для выделенных…", ProcurementBulkSupplier_Click, "Building");
        AddAction("Заявка поставщику…", ProcurementSupplierRequest_Click, "File");
        MovementMenu.Items.Add(new Separator());
        AddAction("Заказ / оплата…", ProcurementOrder_Click, "File", "F4");
        Add("Пришло на склад…", "in", "Download");
        Add("Выдано на объект (через ведомость)…", "out", "Upload");
        MovementMenu.Items.Add(new Separator());
        AddAction("Отдать в цех…", ProcurementToShop_Click, "Settings");
        Add("Изготовлено → на склад…", "in", defaultDocument: "из цеха");
        MovementMenu.Items.Add(new Separator());
        AddAction("Не заказываем", async (_, _) => await ApplyBulkProcAsync(null, "skip", "Статус обновлён"));
        AddAction("По факту / монтажники", async (_, _) => await ApplyBulkProcAsync(null, "fact", "Статус обновлён"));
        AddAction("Сбросить статус", async (_, _) => await ApplyBulkProcAsync(null, "", "Статус сброшен"));
        MovementMenu.Items.Add(new Separator());
        Add("Возврат с объекта…", "back");
        Add("Возврат поставщику…", "back_supplier");
        MovementMenu.PlacementTarget = BuyGrid;
        MovementMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        e.Handled = true;
        MovementMenu.IsOpen = true;
    }

    /// <summary>Run the multi-position Python-compatible movement flow.</summary>
    private void AddMovementFlow(ProcurementPreviewRow row, string kind) =>
        _ = OpenMovementDialogAsync(row, kind);

    private long ObjectIdOfPosition(long positionId)
    {
        using var conn = SqlServerDatabase.Open(_connectionString);
        using var cmd = new SqlCommand(
            "SELECT o.id FROM dbo.positions p JOIN dbo.systems sy ON sy.id=p.system_id " +
            "JOIN dbo.contracts c ON c.id=sy.contract_id JOIN dbo.objects o ON o.id=c.object_id " +
            "WHERE p.id=@id", conn);
        cmd.Parameters.AddWithValue("@id", positionId);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L, CultureInfo.InvariantCulture);
    }

    private void SetStatus(string message)
    {
        if (IsLoaded) FooterText.Text = message;
    }
    // Legacy name-only import has been replaced with the strict sheet preview flow.
    private void ImportPlan_Click(object sender, RoutedEventArgs e) =>
        ProcurementImportSheet_Click(sender, e);

}
