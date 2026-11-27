using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// WPF estimate editor: writes are queued away from the UI thread. The data shown while a
/// save is pending is optimistic; the last completed save reloads the authoritative totals.
/// </summary>
public partial class EstimateView : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly string _sqlConnectionString;
    private string _serverRevision = "";
    // A SQL write receipt can advance _serverRevision before the visible
    // estimate has been reconciled. Only a completed full readback sets this.
    private string _publishedEstimateRevision = "";
    /// <summary>Текущая серверная ревизия сметы (для кэша документов).</summary>
    public string ServerRevision => _serverRevision;
    internal bool CacheReadOrWriteInProgress => _refreshing || _reloadCancellation is not null || Volatile.Read(ref _pendingWrites) > 0 ||
        _cellSaveQueue.PendingCount > 0;
    internal bool HasUnsettledCacheWrites => Volatile.Read(ref _pendingWrites) > 0 ||
        _cellSaveQueue.PendingCount > 0 || _cellSaveQueue.Blocked || _saveAwaitingReadback || _saveError is not null;
    // Eviction is stricter than ordinary estimate revision validation: nested
    // editors can own independent operations and uncommitted input.
    internal bool HasNestedCacheActivity => _summaryBusy || _totalsBusy || _materialsBusy ||
        _summaryLoading || _totalsLoading || _materialsLoading || _inlineStagesLoading ||
        _contractProcurementPane?.HasCacheActivity == true ||
        _contractShipmentsPane?.HasCacheActivity == true ||
        ContractActsControl?.HasCacheActivity == true ||
        HasGridEdit(SummaryGrid) || HasGridEdit(TotalsGrid) || HasGridEdit(MaterialsGrid);

    private static bool HasGridEdit(DataGrid? grid) =>
        grid?.ItemsSource is not null &&
        System.Windows.Data.CollectionViewSource.GetDefaultView(grid.ItemsSource)
            is System.ComponentModel.IEditableCollectionView { IsEditingItem: true };

    internal bool CanTransferDocument => !CacheReadOrWriteInProgress && !HasUnsettledCacheWrites &&
        !HasActiveEstimateEdit() && !HasNestedCacheActivity;
    // Row counts are an inexpensive proxy for retained editor size, not bytes.
    // Include the independently materialized materials/summary projections.
    internal long CacheRowWeight => (long)_rows.Count + _materialsBuilt.Count + SummaryRecs.Count +
        (ReferenceEquals(_totalsSourceSummary, _costSummaryData) ? 0 : _totalsSourceSummary?.Rows.Count ?? 0);

    internal bool CanReleaseCachedView => !IsLoaded && !CacheReadOrWriteInProgress &&
        !HasUnsettledCacheWrites && !_reloadTimer.IsEnabled && !HasActiveEstimateEdit() &&
        !HasNestedCacheActivity;

    internal bool CanReuseCacheRevision(string revision) => _estimate is not null && !_serverConflict &&
        !CacheReadOrWriteInProgress && !HasUnsettledCacheWrites &&
        !string.IsNullOrEmpty(_serverRevision) && string.Equals(_serverRevision, revision, StringComparison.Ordinal);
    private bool _serverConflict;
    private readonly int _contractId;
    private readonly int _objectId;
    private readonly bool _isContract;
    private readonly bool _isKp;
    private readonly DocRow _doc;
    private readonly HashSet<int> _collapsedSectionIds = new();
    // Shared with other open estimate windows for this SQL contract.
    // The wrapper adds the lazy undo baseline while the lock is held.
    private readonly UndoAwareWriteLock _writeLock;
    private readonly ContractCellSaveQueue _cellSaveQueue;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private static readonly SemaphoreSlim ReloadSqlSlots = new(3, 3);
    private CancellationTokenSource? _reloadCancellation;
    private readonly EstimateReloadGate _reloadGate = new();
    private readonly DispatcherTimer _reloadTimer;
    private DispatcherTimer? _estimateSearchTimer;
    private bool _searchPending;
    private bool _detached;
    private bool _reloadAfterAttach;
    private List<EditableEstimateRow> _rows = new();
    private EstimateRowViewIndex? _rowViewIndex;
    private EstimateRowViewIndex RowViewIndex =>
        _rowViewIndex is { } index && ReferenceEquals(index.Rows, _rows)
            ? index : (_rowViewIndex = new EstimateRowViewIndex(_rows));
    private readonly EstimateUiSettings _uiSettings = EstimateUiSettings.Load();
    private bool _uiSettingsReady;
    private string _visibleEstimateMode = "estimate";
    private EstimateDocument? _estimate;
    private int _pendingWrites;
    // Set when a local write starts: the next accepted reload records the new
    // undo checkpoint, while plain opens/background reloads skip the snapshot.
    private int _undoCheckpointNeeded;
    private int? _focusAfterReload;
    private bool _editAfterReload;
    private int? _focusSystemAfterReload;
    private int? _positionFromSearch;
    private bool _refreshing;
    private string? _saveError;
    private readonly Dictionary<DataGridColumn, DataGridLength> _defaultEstimateWidths;
    private readonly List<DataGridColumn> _defaultEstimateColumns;
    private readonly int[] _defaultDisplayIndexes;

    // SQL Server is the single supported storage backend for the C# editor.
    public EstimateView(DbConnection conn, DocRow doc, string sqlConnectionString)
    {
        _contractId = doc.Id;
        _objectId = doc.ObjectId;
        _doc = doc;
        _isContract = doc.Kind is "main" or "dop";
        _isKp = string.Equals(doc.Kind, "kp", StringComparison.OrdinalIgnoreCase);
        _sqlConnectionString = !string.IsNullOrWhiteSpace(sqlConnectionString)
            ? sqlConnectionString
            : throw new InvalidOperationException("Для редактора требуется SQL Server.");
        // Opening an estimate must not perform synchronous network I/O on WPF's
        // dispatcher thread. Revision and data arrive together in the async loader.
        _serverRevision = "";
        _writeLock = new UndoAwareWriteLock(
            ContractSqlWriteLock.For(_sqlConnectionString, _contractId),
            EnsureUndoBaselineAsync);
        _cellSaveQueue = new ContractCellSaveQueue(WriteCellOnBackgroundAsync);
        InitializeComponent();
        MoveContractItem.Visibility=doc.Kind=="main"?Visibility.Visible:Visibility.Collapsed;
        // Python kp_table: «Обоснование» — последний столбец сметы.
        BasisColumn.DisplayIndex = EstimateGrid.Columns.Count - 1;
        BasisColumn.CanUserReorder = false;
        _defaultEstimateWidths = EstimateGrid.Columns.ToDictionary(column => column, column => column.Width);
        _defaultEstimateColumns = EstimateGrid.Columns.ToList();
        _defaultDisplayIndexes = EstimateGrid.Columns.Select(column => column.DisplayIndex).ToArray();
        RestoreEstimateUiSettings();
        ApplyEstimateZoom();
        _uiSettingsReady = true;
        InitializeEstimateColumnLayout();
        TableColumnMenu.InstallHeaderMenu(EstimateGrid, header => ShowEstimateColumns(header, PlacementMode.MousePoint, includePurchase: true));
        KpDocumentTabs.Visibility = _isContract ? Visibility.Collapsed : Visibility.Visible;
        ContractDocumentTabs.Visibility = _isContract ? Visibility.Visible : Visibility.Collapsed;
        HintBar.Visibility = _uiSettings.EstimateHintHidden ? Visibility.Collapsed : Visibility.Visible;
        PhaseButton.Visibility = _isKp ? Visibility.Visible : Visibility.Collapsed;

        _reloadTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _reloadTimer.Tick += RefreshAfterSave;
        _estimateSearchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _estimateSearchTimer.Tick += (_, _) => ApplyPendingSearch();
        Unloaded += (_, _) =>
        {
            _detached = true;
            _estimateSearchTimer.Stop();
            if (!HasUnsettledCacheWrites)
            {
                _reloadAfterAttach |= _refreshing || _reloadCancellation is not null || _reloadTimer.IsEnabled;
                _reloadTimer.Stop();
                _reloadGate.NextRequest(); // discard an in-flight read of a detached, clean editor
                _reloadCancellation?.Cancel(); // preserve mandatory readback on detach
                CancelSummaryRead();
                CancelMaterialsRead();
                CancelTotalsRead();
            }
        };
        Loaded += (_, _) =>
        {
            _detached = false;
            if (_searchPending) _estimateSearchTimer.Start();
            if (_reloadAfterAttach)
            {
                _reloadAfterAttach = false;
                RequestRefresh();
            }
            else if (_visibleEstimateMode == "summary" && _estimate is not null &&
                string.IsNullOrWhiteSpace(_summaryRevision) && !HasUnsettledCacheWrites)
                _ = LoadCostSummaryAsync();
            else if (_visibleEstimateMode == "materials" && _estimate is not null &&
                !_materialsBusy && !HasUnsettledCacheWrites &&
                (string.IsNullOrWhiteSpace(_materialsRevision) || _materialsRevision != _serverRevision))
                _ = LoadMaterialsAsync();
            else if (_visibleEstimateMode == "totals" && _estimate is not null &&
                !_totalsBusy && !HasUnsettledCacheWrites &&
                (string.IsNullOrWhiteSpace(_totalsRevision) || _totalsRevision != _serverRevision))
                _ = LoadCostTotalsAsync();
        };
        SetStatus("Загрузка сметы из SQL Server…", false);
        _ = ReloadDocumentAsync();
    }

    private void SetDocument(EstimateDocument est)
    {
        _estimate = est;
        InvalidateCostSummary();
        InvalidateCostTotals();
        CancelMaterialsRead(); // a new authoritative estimate supersedes a clean in-flight read
        var selectedId = (EstimateGrid.SelectedItem as EditableEstimateRow)?.Id;
        // Full authoritative readback stays mandatory. Reuse only equal
        // source records, never infer a committed value from the editor.
        var nextRows = EstimateRowViewIndex.Reconcile(_rows, est.Rows, !_inlineStagesOn);
        if (_rowViewIndex is { } index &&
            (!ReferenceEquals(index.Rows, _rows) || !index.TryAdvance(nextRows)))
            _rowViewIndex = null;
        _rows = nextRows;
        foreach (var section in _rows.Where(r => r.IsSystem))
            section.IsCollapsed = _collapsedSectionIds.Contains(section.Source.SystemId);
        foreach (var summary in _rows.Where(r => r.IsAreaSummary))
            summary.IsCollapsed = !_expandedAreaSummaries.Contains(summary.Id);
        ApplyFilter();
        var focusId = _focusAfterReload ?? selectedId;
        var focusSystem = _focusSystemAfterReload;
        var editAfterReload = _editAfterReload;
        _editAfterReload = false;
        _focusAfterReload = null;
        _focusSystemAfterReload = null;
        EditableEstimateRow? selected = focusSystem is int systemId
            ? RowViewIndex.FindSection(systemId)
            : focusId is int id ? RowViewIndex.ByPositionId.GetValueOrDefault(id) : null;
        if (selected is not null)
        {
            if (!ReferenceEquals(EstimateGrid.SelectedItem, selected) &&
                (focusSystem is not null || selectedId != focusId || !EstimateGrid.SelectedItems.Contains(selected)))
                EstimateGrid.SelectedItem = selected;
            if (focusSystem is not null || selectedId != focusId)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    // A queued focus request must not steal focus or edit a row
                    // after navigation, another reload or a change of selection.
                    if (_detached || !EstimateGrid.IsVisible ||
                        !ReferenceEquals(EstimateGrid.SelectedItem, selected) ||
                        !EstimateGrid.Items.Contains(selected)) return;
                    EstimateGrid.ScrollIntoView(selected);
                    if (!selected.IsSystem && editAfterReload)
                    {
                        EstimateGrid.CurrentCell = new DataGridCellInfo(selected, EstimateGrid.Columns[1]);
                        EstimateGrid.BeginEdit();
                    }
                }), DispatcherPriority.Background);
        }
        AddRowButton.IsEnabled = !est.Locked;
        WorkColumn.Visibility = est.SmrMode == "pos" ? Visibility.Visible : Visibility.Collapsed;
        WorkTotalColumn.Visibility = est.SmrMode == "pos" ? Visibility.Visible : Visibility.Collapsed;
        ConfigureSpecialColumns();
        RequestEstimateColumnFit();

        // Keep the full legal title in the tooltip; use the current snapshot, not the cached navigation row.
        var isAnnex = est.KindTitle == "Договор";
        TitleText.Text = est.AnnexDisplayTitle ?? est.Title;
        SubtitleText.Text = est.ObjectName;
        DocumentKindText.Text = isAnnex ? "Приложение" : est.KindTitle;
        CompanyText.Text = string.IsNullOrWhiteSpace(est.Company) ? "организация не указана" : est.Company;
        CompanyChip.Visibility = string.IsNullOrWhiteSpace(est.Company)
            ? Visibility.Collapsed : Visibility.Visible;
        LockChip.Visibility = est.Locked ? Visibility.Visible : Visibility.Collapsed;
        // Python has no separate counter row in the header. Keep the full
        // title and counts available without expanding or crowding the toolbar.
        TitleText.ToolTip = $"{est.Title}\n" +
            $"{est.Positions} {CountForm(est.Positions, "позиция", "позиции", "позиций")} · " +
            $"{est.Systems} {CountForm(est.Systems, "система", "системы", "систем")}";
        EditTitleButton.IsEnabled = !est.Locked;
        UpdatePhaseButton();
        TransferRatesButton.Visibility = _isKp ? Visibility.Visible : Visibility.Collapsed;
        SetSmrControls(est);

        MaterialsText.Text = N2(est.VatTop ? est.Totals.Materials : est.Statistics.Materials);
        MaterialsCaption.Text = est.VatTop ? "Строки сметы (без НДС)" : "Материалы";
        SmrCard.Visibility = est.VatTop ? Visibility.Collapsed : Visibility.Visible;
        LayoutTotalsCards();
        if (est.VatTop)
            MaterialsText.ToolTip = "Работы и материалы — строками одной сметы, как у заказчика";
        else if (est.Statistics.WorkRows != 0)
            MaterialsText.ToolTip = $"Без строк «Монтаж …» ({N2(est.Statistics.WorkRows)}) — они считаются как СМР";
        else
            MaterialsText.ClearValue(ToolTipProperty);

        TransportCard.Visibility = Visibility.Visible;
        TransportEdit.Text = est.Totals.Transport == 0m ? "0" : N2(est.Totals.Transport);
        // Python kp_editor._fill_totals: keep a zero PNR field only in position-based SMR.
        // Other modes can enter the amount through the dedicated «Итоги» tab.
        PnrCard.Visibility = est.SmrMode == "pos" || est.Totals.Pnr != 0m
            ? Visibility.Visible : Visibility.Collapsed;
        PnrEdit.Text = est.Totals.Pnr == 0m ? "0" : N2(est.Totals.Pnr);
        SmrEdit.Text = est.SmrMode == "percent"
            ? NumberText.QtyText(est.SmrPercent)
            : N2(string.IsNullOrWhiteSpace(est.SmrMode) || est.SmrMode == "sum"
                ? est.Statistics.Smr : est.Totals.Smr);

        var sourceCheck=est.SourceCheck;
        SourceCheckChip.Visibility=sourceCheck.Matches==false?Visibility.Visible:Visibility.Collapsed;
        SourceCheckChip.ToolTip=sourceCheck.Details.Length>0?sourceCheck.Details:null;
        var hasDiscount = est.Totals.DiscountPercent != 0m;
        TotalCaption.Text = hasDiscount ? "ИТОГО со скидкой" : "ИТОГО с НДС";
        DiscountTotalText.Visibility = hasDiscount ? Visibility.Visible : Visibility.Collapsed;
        DiscountTotalText.Text = hasDiscount
            ? $"Скидка {est.Totals.DiscountPercent:0.#########}%: −{N2(est.Totals.DiscountAmount)} ₽" : "";
        TotalText.Text = N2(est.Totals.Total);
        VatText.Text = est.VatTop
            ? $"НДС {Rate(est.VatRate)} сверху: {N2(est.Totals.Vat)} ₽"
            : $"В том числе НДС {Rate(est.VatRate)}: {N2(est.Totals.Vat)} ₽";
        TotalsSummary.Text = est.VatTop
            ? $"ИТОГО без НДС {N2(est.Totals.Net)}   ·   НДС {Rate(est.VatRate)} {N2(est.Totals.Vat)}   ·   ИТОГО с НДС {N2(est.Totals.Total)} ₽"
            : $"Материалы {N2(est.Statistics.Materials)}   ·   СМР {N2(est.Statistics.Smr)}" +
              (est.Statistics.WorkRows != 0 ? $" (в т.ч. монтаж строками {N2(est.Statistics.WorkRows)})" : "") +
              (est.Totals.Pnr != 0 ? $"   ·   ПНР {N2(est.Totals.Pnr)}" : "") +
              $"   ·   ИТОГО с НДС {N2(est.Totals.Total)} ₽";
        if (hasDiscount)
            TotalsSummary.Text += $"   ·   {DiscountTotalText.Text}";

        // Undo snapshot was captured with the estimate on the background thread
        // and applied by ReloadDocumentAsync, after checking the SQL revision.
        if (_visibleEstimateMode == "summary") _ = LoadCostSummaryAsync();
        if (_visibleEstimateMode == "totals") _ = LoadCostTotalsAsync();
        if (_visibleEstimateMode == "materials") _ = LoadMaterialsAsync();
        if (_inlineStagesOn)
            _ = RefreshInlineStagesAsync();
        else
            ApplyRequestedInlineStages();
    }


    private void ToggleSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: EditableEstimateRow section } ||
            !section.IsExpandable) return;

        if (section.IsGroup)
        {
            if (!_expandedGroupKeys.Add(section.GroupKey))
                _expandedGroupKeys.Remove(section.GroupKey);
            ApplyFilter();
            e.Handled = true;
            return;
        }
        if (section.IsTypeHeader)
        {
            if (!_collapsedTypeKeys.Add(section.TypeKey))
                _collapsedTypeKeys.Remove(section.TypeKey);
            ApplyFilter();
            e.Handled = true;
            return;
        }
        if (section.IsAreaSummary)
        {
            if (!_expandedAreaSummaries.Add(section.Id))
                _expandedAreaSummaries.Remove(section.Id);
            section.IsCollapsed = !_expandedAreaSummaries.Contains(section.Id);
            ApplyFilter();
            e.Handled = true;
            return;
        }
        var id = section.Source.SystemId;
        if (!_collapsedSectionIds.Add(id)) _collapsedSectionIds.Remove(id);
        section.IsCollapsed = _collapsedSectionIds.Contains(id);
        ApplyFilter();
        e.Handled = true;
    }

    // The five original contract sections share the same document workspace:
    // real SQL Server-backed panes are loaded only on first activation.
    private readonly ContractWorkspaceTabs _contractTabs = new();
    private string _activeContractWorkspace => _contractTabs.Page;
    private ProcurementPane? _contractProcurementPane;
    private ShipmentsPane? _contractShipmentsPane;
    private ContractActsWorkspace? ContractActsControl => ContractActsPanel?.Child as ContractActsWorkspace;

    internal static ContractActsWorkspace EnsureContractActsWorkspace(Border host)
    {
        if (host.Child is ContractActsWorkspace existing) return existing;
        var workspace = new ContractActsWorkspace();
        host.Child = workspace;
        return workspace;
    }

    private void ContractEstimate_Checked(object sender, RoutedEventArgs e) =>
        SetContractWorkspace("estimate");

    private void ContractCosts_Checked(object sender, RoutedEventArgs e) =>
        SetContractWorkspace("costs");

    private void ContractProcurement_Checked(object sender, RoutedEventArgs e) =>
        SetContractWorkspace("procurement");

    private void ContractShipments_Checked(object sender, RoutedEventArgs e) =>
        SetContractWorkspace("shipments");

    private void ContractActs_Checked(object sender, RoutedEventArgs e) =>
        SetContractWorkspace("acts");

    /// <summary>
    /// Keep the actual editors in place instead of opening another Window.
    /// The standalone-window wrappers still serve legacy context-menu entry points.
    /// Re-selecting a cached page refreshes its data from SQL Server.
    /// </summary>
    private void SetContractWorkspace(string page)
    {
        // Checked events fire during InitializeComponent, before the named
        // content hosts exist. Never attempt a DB read until the document loads.
        if (!_isContract || _estimate is null || ContractActsPanel is null ||
            ContractCostsPanel is null || ContractProcurementPanel is null ||
            ContractShipmentsPanel is null)
            return;

        var was = _activeContractWorkspace;
        _contractTabs.SelectPage(page);
        _visibleEstimateMode = _contractTabs.VisibleMode;
        var estimate = page == "estimate";
        var acts = page == "acts";
        EstimateTablePanel.Visibility = estimate ? Visibility.Visible : Visibility.Collapsed;
        ContractActsPanel.Visibility = acts ? Visibility.Visible : Visibility.Collapsed;
        // CostSummaryPane (contract) replaced by the same panels as KP (Materials/Summary/Totals).
        ContractCostsPanel.Visibility = Visibility.Collapsed;
        ContractProcurementPanel.Visibility = page == "procurement" ? Visibility.Visible : Visibility.Collapsed;
        ContractShipmentsPanel.Visibility = page == "shipments" ? Visibility.Visible : Visibility.Collapsed;
        // Main tabs stay visible; only the dedicated second row opens under Costs.
        KpDocumentTabs.Visibility = Visibility.Collapsed;
        ContractCostNavigation.Visibility = _contractTabs.ShowCostNavigation
            ? Visibility.Visible : Visibility.Collapsed;
        MaterialsPanel.Visibility = Visibility.Collapsed;
        SummaryPanel.Visibility = Visibility.Collapsed;
        TotalsPanel.Visibility = Visibility.Collapsed;
        EstimateToolsBar.Visibility = estimate ? Visibility.Visible : Visibility.Collapsed;
        TotalsFooterPanel.Visibility = estimate ? Visibility.Visible : Visibility.Collapsed;
        HintBar.Visibility = estimate && !_uiSettings.EstimateHintHidden
            ? Visibility.Visible : Visibility.Collapsed;
        LockedBar.Visibility = estimate && _estimate.Locked
            ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            switch (page)
            {
                case "estimate":
                    // Revalidate the published estimate on SQL Server. A clean
                    // unchanged snapshot need not be mapped and rebound again.
                    if (was != "estimate") _ = ReloadDocumentAsync(allowUnchanged: true);
                    break;
                case "acts":
                    EnsureContractActsWorkspace(ContractActsPanel).Bind(_sqlConnectionString, _contractId, _estimate.Title);
                    break;
                case "costs":
                    // Keep the selected subsection when returning from another main tab.
                    // Apply explicitly even if the RadioButton was already checked.
                    ApplyEstimateMode(_contractTabs.CostSection);
                    break;
                case "procurement":
                    if (_contractProcurementPane is null)
                        ContractProcurementHost.Content = _contractProcurementPane =
                            new ProcurementPane(_sqlConnectionString, _contractId, _estimate.Title);
                    else if (was != page) _contractProcurementPane.RefreshOnTabReturn();
                    break;
                case "shipments":
                    if (_contractShipmentsPane is null)
                        ContractShipmentsHost.Content = _contractShipmentsPane =
                            new ShipmentsPane(_sqlConnectionString, _objectId, _estimate.ObjectName);
                    else if (was != page) _contractShipmentsPane.RefreshOnTabReturn();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message,
                "Не удалось открыть раздел документа", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ViewMode_Checked(object sender, RoutedEventArgs e)
    {
        // Radio buttons can fire Checked before all named controls exist.
        if (_isContract || sender is not RadioButton tab) return;
        ApplyEstimateMode(tab.Tag as string ?? "estimate");
    }

    private void ContractCostMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isContract || sender is not RadioButton { Tag: string mode } ||
            !_contractTabs.SelectCostSection(mode))
            return;
        ApplyEstimateMode(mode);
    }

    private void ApplyEstimateMode(string mode)
    {
        if (EstimateTablePanel is null || MaterialsPanel is null ||
            SummaryPanel is null || TotalsPanel is null)
            return;
        _visibleEstimateMode = mode;
        EstimateTablePanel.Visibility = _visibleEstimateMode == "estimate" ? Visibility.Visible : Visibility.Collapsed;
        MaterialsPanel.Visibility = _visibleEstimateMode == "materials" ? Visibility.Visible : Visibility.Collapsed;
        SummaryPanel.Visibility = _visibleEstimateMode == "summary" ? Visibility.Visible : Visibility.Collapsed;
        // Python cost_view: вкладка «Материалы» заменяет и панель инструментов, и итоговые
        // плитки сметы своими (cost_view mat_page: bar + hint + tiles).
        var estimateChrome = _visibleEstimateMode == "estimate";
        EstimateToolsBar.Visibility = estimateChrome ? Visibility.Visible : Visibility.Collapsed;
        if (MaterialsHintBar is not null)
            MaterialsHintBar.Visibility = _visibleEstimateMode == "materials" && !_uiSettings.MaterialsHintHidden
                ? Visibility.Visible : Visibility.Collapsed;
        if (_visibleEstimateMode != "estimate")
        {
            HintBar.Visibility = Visibility.Collapsed;
            LockedBar.Visibility = Visibility.Collapsed;
            TotalsFooterPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            HintBar.Visibility = _uiSettings.EstimateHintHidden ? Visibility.Collapsed : Visibility.Visible;
            LockedBar.Visibility = _estimate is { Locked: true } ? Visibility.Visible : Visibility.Collapsed;
            TotalsFooterPanel.Visibility = _isContract ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_visibleEstimateMode == "summary" && _estimate is not null) _ = LoadCostSummaryAsync();
        if (_visibleEstimateMode == "totals" && _estimate is not null) _ = LoadCostTotalsAsync();
        if (_visibleEstimateMode == "materials") _ = LoadMaterialsAsync();
        TotalsPanel.Visibility = _visibleEstimateMode == "totals" ? Visibility.Visible : Visibility.Collapsed;
        // The "Итоги" page does not reset the user's independent
        // bottom-footer visibility preference (kp_editor.toggle_totals).
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        ExtraActionsExpander.IsOpen = !ExtraActionsExpander.IsOpen;
        if (ExtraActionsExpander.IsOpen)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
            {
                if (ExtraActionsExpander.IsOpen)
                    ActionsMenuPanel.Children.OfType<Button>().FirstOrDefault(b => b.IsVisible && b.IsEnabled)?.Focus();
            }));
    }

    private void ActionsMenu_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExtraActionsExpander.IsOpen = false;
            MoreActionsButton.Focus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Up && e.Key != Key.Down && e.Key != Key.Home && e.Key != Key.End) return;
        var available = ActionsMenuPanel.Children.OfType<Button>()
            .Where(b => b.IsVisible && b.IsEnabled).ToArray();
        if (available.Length == 0) return;
        var current = Array.FindIndex(available, b => b.IsKeyboardFocusWithin);
        var next = e.Key switch
        {
            Key.Home => 0,
            Key.End => available.Length - 1,
            Key.Up => current < 0 ? available.Length - 1 : (current + available.Length - 1) % available.Length,
            _ => (current + 1) % available.Length
        };
        available[next].Focus();
        available[next].BringIntoView();
        e.Handled = true;
    }

    private void AddOptions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || _estimate?.Locked != false) return;
        var row = EstimateGrid.SelectedItem as EditableEstimateRow;
        var writable = !_serverConflict && Volatile.Read(ref _pendingWrites) == 0;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };

        void Add(string title, RoutedEventHandler handler, bool available = true, string shortcut = "", string glyph = "Plus")
        {
            var item = ContextMenuIcons.Create(title, glyph, writable && available, shortcut);
            item.Click += handler;
            menu.Items.Add(item);
        }

        Add("Строку", AddRow_Click, shortcut: "Insert");
        Add("Комплектующее к строке (без цены)", AddComponent_Click,
            row is { IsSystem: false, IsVirtual: false }, "Shift+Insert");
        Add("Из справочника…", AddFromCatalog_Click, shortcut: "Ctrl+K", glyph: "Book");
        menu.Items.Add(new Separator());
        Add("Систему (П1, В3, К1…)", AddSection_Click, shortcut: "Ctrl+Shift+Insert", glyph: "FolderPlus");
        Add("Подраздел в систему", AddSubsection_Click,
            row?.IsVirtual != true, "Ctrl+Alt+Insert", "FolderPlus");
        var copyRevision = _serverRevision;
        Add("Копию системы («П2 как П1»)…", (s, args) =>
        {
            if (copyRevision != _serverRevision || !ReferenceEquals(row, EstimateGrid.SelectedItem))
            {
                SetStatus("Выбор или данные изменились. Откройте меню снова.", true);
                return;
            }
            CopySection_Click(s, args);
        }, CopySourceSystem(row) is not null, glyph: "Duplicate");
        menu.Items.Add(new Separator());
        Add("Строки из Excel (скопированные)", PasteRows_Click, shortcut: "Ctrl+V", glyph: "Upload");
        Add("Загрузить строки из файла Excel…", ImportFile_Click, glyph: "Upload");
        menu.IsOpen = true;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        new HelpDialog(Window.GetWindow(this)).ShowDialog();
    }

    private void EstimateSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_estimateSearchTimer is null || _estimate is null) return;
        _searchPending = true;
        _estimateSearchTimer.Stop();
        if (string.IsNullOrWhiteSpace(EstimateSearch.Text))
            ApplyPendingSearch(); // Clear is immediate unless a grid edit is active
        else
            _estimateSearchTimer.Start();
    }

    private void ApplyPendingSearch()
    {
        _estimateSearchTimer?.Stop();
        // Replacing ItemsSource during an edit could lose an uncommitted cell.
        if (System.Windows.Data.CollectionViewSource.GetDefaultView(EstimateGrid.ItemsSource)
            is System.ComponentModel.IEditableCollectionView { IsEditingItem: true })
        {
            if (IsLoaded) _estimateSearchTimer?.Start();
            return;
        }
        ApplyFilter();
    }

    private void ClearEstimateSearch_Click(object sender, RoutedEventArgs e)
    {
        EstimateSearch.Clear();
        EstimateSearch.Focus();
    }

    internal void FocusPositionFromSearch(int positionId)
    {
        _positionFromSearch = positionId;
        SelectPositionFromSearch();
    }

    private void SelectPositionFromSearch()
    {
        if (_estimate is null || _positionFromSearch is not int id) return;
        _positionFromSearch = null;
        var row = RowViewIndex.ByPositionId.GetValueOrDefault(id);
        if (row is null)
        {
            SetStatus("Позиция больше недоступна в этой смете", true);
            return;
        }

        (_isContract ? ContractEstimateTab : EstimateModeTab).IsChecked = true;
        EstimateSearch.Clear();
        // Explicit navigation must not wait for the typing debounce.
        ApplyFilter();
        if (!EstimateGrid.Items.Contains(row))
        {
            ResetTemporaryView();
            var system = RowViewIndex.FindSection(row.Source.SystemId);
            void ExpandSection(EditableEstimateRow? section)
            {
                if (section is null) return;
                _collapsedSectionIds.Remove(section.Source.SystemId);
                section.IsCollapsed = false;
            }
            ExpandSection(system);
            if (system?.ParentSystemId is int parentSystem)
                ExpandSection(RowViewIndex.FindSection(parentSystem));
            if (row.Source.ParentPositionId is int parent &&
                RowViewIndex.ByPositionId.GetValueOrDefault(parent) is { IsAreaSummary: true } summary)
            {
                _expandedAreaSummaries.Add(parent);
                summary.IsCollapsed = false;
            }
            InlineStagesFilter.SelectedIndex = 0;
            ApplyFilter();
        }

        if (!EstimateGrid.Items.Contains(row))
        {
            SetStatus("Не удалось показать найденную позицию в таблице", true);
            return;
        }
        EstimateGrid.SelectedItem = row;
        EstimateGrid.CurrentCell = new DataGridCellInfo(row, NameColumn);
        Dispatcher.BeginInvoke(() =>
        {
            if (!EstimateGrid.IsVisible || !ReferenceEquals(EstimateGrid.SelectedItem, row)) return;
            EstimateGrid.ScrollIntoView(row, NameColumn);
            EstimateGrid.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyFilter(bool expandAllGroups = false)
    {
        _estimateSearchTimer?.Stop();
        _searchPending = false;
        if (_estimate is null) return;
        if (ApplyGroupedFilter(expandAllGroups)) return;

        // The temporary view is a read-only projection of authoritative
        // SQL-loaded rows. SortKey and position IDs remain unchanged.
        var projected = EstimateViewProjection.Apply(
            _estimate.Rows, EstimateSearch.Text, _noPriceTemporaryView,
            _temporarySort, _flatTemporaryView, _frozenTemporaryOrder);
        // Preserve the visible order of existing rows after saving a price
        // (Python _view_key). A manual view change explicitly resets ranks.
        if (_temporarySort != EstimateViewProjection.DocumentOrder)
            foreach (var row in projected.Where(row => !row.IsSystem && !row.IsSub))
                if (!_frozenTemporaryOrder.ContainsKey(row.Id))
                    _frozenTemporaryOrder[row.Id] = _frozenTemporaryOrder.Count;
        var index = RowViewIndex;

        // A stage filter must still reveal rows hidden inside collapsed
        // systems, or the user could see a blank result with no expandable
        // header to reach the undistributed positions.
        var honorFolds = !_flatTemporaryView &&
            string.IsNullOrWhiteSpace(EstimateSearch.Text) &&
            (!_inlineStagesOn || _inlineStagesFilter == StageAllocationPresentation.All);
        var rootFolded = false;
        var subFolded = false;
        var visible = new List<EditableEstimateRow>(projected.Count);
        var areaSummaryIds = index.AreaSummaryIds;
        foreach (var row in index.Project(projected, _estimate.Rows))
        {
            if (row.IsSystem && row.Level == 0)
            {
                rootFolded = honorFolds && _collapsedSectionIds.Contains(row.Source.SystemId);
                subFolded = false;
            }
            else if (row.IsSystem && row.Level == 1)
                subFolded = honorFolds && _collapsedSectionIds.Contains(row.Source.SystemId);

            if (rootFolded && row.Level > 0) continue;
            if (subFolded && row.Level > 1) continue;
            if (honorFolds && row.IsSub &&
                row.Source.ParentPositionId is int areaId &&
                areaSummaryIds.Contains(areaId) && !_expandedAreaSummaries.Contains(areaId))
                continue;
            // Prepare only rows surviving search and folds. Stage filtering
            // below needs their current presentation depth to find ancestors.
            index.PrepareDocumentRow(row, _flatTemporaryView,
                EstimateViewProjection.SortField(_temporarySort) == EstimateViewProjection.ByNumber);
            visible.Add(row);
        }

        var selected = EstimateGrid.SelectedItem as EditableEstimateRow;
        visible = ApplyInlineStagesFilter(visible);
        // No collection reset if the filtered projection is identical. WPF
        // then keeps the current cell, selection and scroll position intact.
        if (EstimateGrid.ItemsSource is not IEnumerable<EditableEstimateRow> existing ||
            !existing.SequenceEqual(visible, ReferenceEqualityComparer.Instance))
        {
            if (_inlineStagesOn || !EstimateGridRowUpdates.TryReplace(EstimateGrid, visible))
            {
                EstimateGrid.ItemsSource = EstimateGridRowUpdates.CreateSource(visible);
                if (selected is not null && visible.Contains(selected))
                    EstimateGrid.SelectedItem = selected;
            }
        }

        RefreshTemporaryViewControls();
    }

    private void SetStatus(string message, bool error)
    {
        // Python: status.emit → нижняя строка главного окна (QStatusBar).
        if (!_detached && Window.GetWindow(this) is MainWindow main)
            main.SetEstimateStatus(message, error);
    }

    private async void RefreshAfterSave(object? sender, EventArgs e)
    {
        _reloadTimer.Stop();
        if (_detached && !HasUnsettledCacheWrites) { _reloadAfterAttach = true; return; }
        if (_refreshing || Volatile.Read(ref _pendingWrites) > 0) return;
        if (Keyboard.FocusedElement is TextBox editor && IsInside(editor, EstimateGrid))
        {
            _reloadTimer.Start();
            return;
        }
        await ReloadDocumentAsync();
    }

    /// <summary>
    /// One read at a time. A newer request supersedes an older one; a local
    /// write that completed while SQL was loading invalidates the stale
    /// snapshot. Never overwrite an optimistic edit with an earlier read.
    /// </summary>
    private async Task ReloadDocumentAsync(bool allowUnchanged = false)
    {
        if (_detached && !HasUnsettledCacheWrites) { _reloadAfterAttach = true; return; }
        var request = _reloadGate.NextRequest();
        var previous = _reloadCancellation;
        using var cancellation = new CancellationTokenSource();
        _reloadCancellation = cancellation;
        previous?.Cancel(); // replacement already registered
        var token = cancellation.Token;
        var acquired = false;
        try
        {
            await _reloadLock.WaitAsync(token);
            acquired = true;
            if (!_reloadGate.IsLatest(request)) return;
            if (Volatile.Read(ref _pendingWrites) > 0 || HasActiveEstimateEdit())
            {
                _reloadTimer.Start(); // Last writer will re-arm this timer.
                return;
            }

            _refreshing = true;
            var localRevisionAtStart = _serverRevision;
            // Undo is lazy: only a document edited locally needs its snapshot.
            var needUndoCheckpoint = Volatile.Read(ref _undoCheckpointNeeded) != 0;
            var reusableRevision = allowUnchanged && CanReusePublishedEstimate(_publishedEstimateRevision)
                ? _publishedEstimateRevision : "";
            try
            {
                var loaded = await Task.Run(async () =>
                {
                    await ReloadSqlSlots.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        using var read = await SqlServerDatabase.OpenAsync(_sqlConnectionString, token).ConfigureAwait(false);
                        // Read one coherent document revision: estimate, the cheap
                        // spec-news check and (only after a local edit) the undo
                        // baseline all use the same SQL connection.
                        for (var attempt = 0; attempt < 2; attempt++)
                        {
                            var before = await SqlServerDatabase.RevisionAsync(read, token).ConfigureAwait(false);
                            if (EstimateReloadGate.CanReusePublishedRevision(
                                reusableRevision, localRevisionAtStart, before, needUndoCheckpoint))
                                return (Estimate: (EstimateDocument?)null, Revision: before,
                                    Undo: (UndoSnapshot?)null, SpecHead: (SpecRevisionHead?)null);
                            StartupTrace.Mark("estimate_read_begin");
                            var document = await EstimateReader.LoadSqlAsync(read, _contractId, token).ConfigureAwait(false);
                            StartupTrace.Mark("estimate_read_end");
                            token.ThrowIfCancellationRequested();
                            var specHead = SqlServerSpecUpdate.LatestRevision(read, null, _contractId);
                            token.ThrowIfCancellationRequested();
                            UndoSnapshot? checkpoint = null;
                            if (needUndoCheckpoint && MainWindow.Undo is not null)
                            {
                                StartupTrace.Mark("estimate_undo_read_begin");
                                checkpoint = SqlServerUndo.Snapshot(read, null, _contractId);
                                StartupTrace.Mark("estimate_undo_read_end");
                            }
                            var after = await SqlServerDatabase.RevisionAsync(read, token).ConfigureAwait(false);
                            if (before == after)
                                return (Estimate: (EstimateDocument?)document, Revision: after,
                                    Undo: checkpoint, SpecHead: specHead);
                        }
                        throw new InvalidOperationException(
                            "Смета изменилась во время чтения. Повтори обновление.");
                    }
                    finally { ReloadSqlSlots.Release(); }
                }, token);

                token.ThrowIfCancellationRequested();
                if (HasActiveEstimateEdit())
                {
                    if (_reloadGate.IsLatest(request)) _reloadTimer.Start();
                    return;
                }
                if (!_reloadGate.CanApply(request,
                    Volatile.Read(ref _pendingWrites),
                    localRevisionAtStart, _serverRevision, loaded.Revision))
                {
                    // A newer request is already queued, or the currently
                    // loading snapshot was overtaken by a local cell save.
                    if (_reloadGate.IsLatest(request))
                        _reloadTimer.Start();
                    return;
                }

                if (loaded.Estimate is null)
                {
                    // Recheck local state after await: an edit, pending focus,
                    // nested operation or newer revision may have appeared.
                    if (CanReusePublishedEstimate(loaded.Revision))
                        StartupTrace.Mark("estimate_tab_revision_hit");
                    else if (_reloadGate.IsLatest(request))
                        _reloadTimer.Start(); // retry via mandatory full readback
                    return;
                }

                // A remote revision must never become a new local Undo step.
                // Local writes update _serverRevision before queuing a reload;
                // a different observed revision belongs to another writer.
                var foreignRevision = EstimateUndoRevisionPolicy.IsForeignRevision(
                    _serverRevision, loaded.Revision);
                if (foreignRevision)
                {
                    MainWindow.Undo?.Forget(_contractId);
                    Interlocked.Exchange(ref _undoCheckpointNeeded, 0);
                }
                _serverRevision = loaded.Revision;
                _serverConflict = false;
                var firstLoad = _estimate is null;
                _publishedEstimateRevision = "";
                StartupTrace.Mark("estimate_publish_begin");
                SetDocument(loaded.Estimate);
                StartupTrace.Mark("estimate_publish_end");
                // A confirmed readback replaces any optimistic, unconfirmed cells.
                // It is now safe to unblock the queue; discarded edits are NOT replayed.
                if (_cellSaveQueue.Blocked)
                    _cellSaveQueue.ResumeAfterReadback();
                if (Volatile.Read(ref _pendingWrites) == 0 && !_cellSaveQueue.Blocked)
                    DetachSaveCloseGuard();
                // Only publish a checkpoint after the reload gate accepted its
                // revision, and only when a local write made the document dirty.
                if (!foreignRevision)
                {
                    if (loaded.Undo is not null)
                        MainWindow.Undo?.CheckpointSnapshot(_contractId, loaded.Undo);
                    Interlocked.Exchange(ref _undoCheckpointNeeded, 0);
                }
                UpdateUndoButtons();
                CheckSpecNews(loaded.SpecHead, foreignRevision);
                if (_saveError is not null)
                    SetStatus("Ошибка записи: " + _saveError +
                        ". Данные перечитаны с сервера; неподтверждённые правки не повторялись.", true);
                else if (_saveAwaitingReadback)
                    SetStatus("Сохранено и сверено с SQL Server ✓", false);
                else if (firstLoad)
                    SetStatus(loaded.Estimate.Title, false);
                _saveAwaitingReadback = false;
                _saveError = null;
                SelectPositionFromSearch();
                _publishedEstimateRevision = loaded.Revision;
                // The cache entry was initially added before its rows arrived.
                if (Window.GetWindow(this) is MainWindow main) main.ScheduleEstimateCacheTrim();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Navigation or a newer reload superseded this snapshot.
            }
            catch (Exception ex)
            {
                if (_reloadGate.IsLatest(request) && !token.IsCancellationRequested)
                {
                    _publishedEstimateRevision = "";
                    SetStatus("Ошибка обновления: " + ex.Message, true);
                }
            }
            finally
            {
                _refreshing = false;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation while waiting for the per-view lock.
        }
        finally
        {
            if (ReferenceEquals(_reloadCancellation, cancellation))
                _reloadCancellation = null;
            if (acquired) _reloadLock.Release();
        }
    }

    // Only the return-to-estimate tab may use this shortcut. Explicit
    // refresh, pending writes, stage views and recovery still read everything.
    private bool CanReusePublishedEstimate(string observedRevision) =>
        _estimate is not null && !_detached && !_serverConflict && !_inlineStagesOn &&
        !HasUnsettledCacheWrites && !HasActiveEstimateEdit() && !HasNestedCacheActivity &&
        !_reloadTimer.IsEnabled && !_reloadAfterAttach &&
        _focusAfterReload is null && _focusSystemAfterReload is null &&
        !_editAfterReload && _positionFromSearch is null &&
        EstimateReloadGate.CanReusePublishedRevision(_publishedEstimateRevision,
            _serverRevision, observedRevision, Volatile.Read(ref _undoCheckpointNeeded) != 0);

    private bool HasActiveEstimateEdit() =>
        System.Windows.Data.CollectionViewSource.GetDefaultView(EstimateGrid.ItemsSource)
            is System.ComponentModel.IEditableCollectionView { IsEditingItem: true };

    private static bool IsInside(DependencyObject child, DependencyObject parent)
    {
        for (DependencyObject? node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, parent)) return true;
        return false;
    }


    private void RefreshNow_Click(object sender, RoutedEventArgs e) => RequestRefresh();

    internal void RequestRefresh()
    {
        _reloadTimer.Stop();
        if (_detached && !HasUnsettledCacheWrites) { _reloadAfterAttach = true; return; }
        _reloadTimer.Start();
    }

    private void PurchaseToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (PurchaseColumn is null || PurchaseToggle is null) return;
        PurchaseColumn.Visibility = PurchaseToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _uiSettings.PurchaseVisible = PurchaseToggle.IsChecked == true;
        SaveEstimateUiSettings();
    }

    private void Columns_Click(object sender, RoutedEventArgs e) =>
        ShowEstimateColumns((Button)sender, PlacementMode.Bottom, includePurchase: true);

    private void ShowEstimateColumns(FrameworkElement anchor, PlacementMode placement, bool includePurchase)
    {
        if (HasActiveEstimateEdit()) return;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = placement };
        menu.Items.Add(new MenuItem { Header = "Показывать столбцы", IsEnabled = false });
        bool Allowed(DataGridColumn c) => !_inlineStageColumns.Contains(c) &&
            c != WorkColumn && c != WorkTotalColumn && (includePurchase || c != PurchaseColumn) &&
            (c != BasisColumn || _estimate is { } est && (est.Form == "r4" || est.BasisAllowed));
        var items = new List<(MenuItem Item, DataGridColumn Column)>();
        // Порядок пунктов — как колонки на экране (DisplayIndex), у DataGrid.Columns
        // перечисление идёт в порядке объявления.
        foreach (var column in EstimateGrid.Columns
                     .OrderBy(c => c.DisplayIndex)
                     .Where(Allowed))
        {
            if (column == NumberColumn || column == NameColumn || column == BasisColumn && _estimate?.Form == "r4")
            {
                menu.Items.Add(new MenuItem
                {
                    Header = column.Header,
                    IsCheckable = true,
                    IsChecked = true,
                    IsEnabled = false,
                    StaysOpenOnClick = true,
                    ToolTip = "Этот столбец скрыть нельзя",
                });
                continue;
            }
            var checkItem = new MenuItem
            {
                Header = column == PurchaseColumn ? "Закупочная цена (видна только нам)" : column.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            checkItem.Click += (_, _) =>
            {
                if (HasActiveEstimateEdit() || !Allowed(column) || column == BasisColumn && _estimate?.Form == "r4")
                { checkItem.IsChecked = column.Visibility == Visibility.Visible; return; }
                column.Visibility = checkItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                if (ReferenceEquals(column, PurchaseColumn)) PurchaseToggle.IsChecked = checkItem.IsChecked;
                if (ReferenceEquals(column, AreaUnitColumn)) _uiSettings.AreaUnitVisible = checkItem.IsChecked;
                if (ReferenceEquals(column, AreaTotalColumn)) _uiSettings.AreaTotalVisible = checkItem.IsChecked;
                if (ReferenceEquals(column, BasisColumn) && _estimate?.Form != "r4")
                    _uiSettings.BasisVisible = checkItem.IsChecked;
                RefreshAreaColumnValues();
                SaveEstimateUiSettings();
            };
            items.Add((checkItem, column));
            menu.Items.Add(checkItem);
        }
        menu.Items.Add(new Separator());
        var showAll = new MenuItem { Header = "Показать все столбцы" };
        showAll.Click += (_, _) =>
        {
            if (HasActiveEstimateEdit()) return;
            foreach (var (item, column) in items)
            {
                if (!Allowed(column) || column == BasisColumn && _estimate?.Form == "r4") continue;
                column.Visibility = Visibility.Visible;
                item.IsChecked = true;
                if (column == PurchaseColumn) PurchaseToggle.IsChecked = true;
                if (column == AreaUnitColumn) _uiSettings.AreaUnitVisible = true;
                if (column == AreaTotalColumn) _uiSettings.AreaTotalVisible = true;
                if (column == BasisColumn && _estimate?.Form != "r4") _uiSettings.BasisVisible = true;
            }
            RefreshAreaColumnValues();
            SaveEstimateUiSettings();
        };
        menu.Items.Add(showAll);
        var resetWidths = new MenuItem { Header = "Вернуть ширину столбцов по умолчанию" };
        resetWidths.Click += (_, _) =>
        {
            if (HasActiveEstimateEdit()) return;
            _uiSettings.ColumnWidths = new();
            foreach (var (column, width) in _defaultEstimateWidths)
                column.Width = width;
            RequestEstimateColumnFit();
            SaveEstimateUiSettings();
        };
        menu.Items.Add(resetWidths);
        var resetOrder = new MenuItem { Header = "Вернуть порядок столбцов" };
        resetOrder.Click += (_, _) =>
        {
            if (HasActiveEstimateEdit()) return;
            _uiSettings.ColumnOrder = new();
            ApplyEstimateOrder(Array.Empty<string>());
            SaveEstimateUiSettings();
        };
        menu.Items.Add(resetOrder);
        menu.IsOpen = true;
    }

    private void ToggleTotals_Click(object sender, RoutedEventArgs e)
    {
        SetTotalsHidden(TotalsCards.Visibility == Visibility.Visible);
        SaveEstimateUiSettings();
    }

    private void SetTotalsHidden(bool hidden)
    {
        TotalsCards.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        TotalsSummary.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        ToggleTotalsButton.Content = hidden ? "Показать итоги" : "Скрыть итоги";
        ToggleTotalsButton.ContentTemplate = (DataTemplate)FindResource(hidden ? "IconLabelArrowUp" : "IconLabelArrowDown");
        ToggleTotalsButton.Foreground = (Brush)new BrushConverter().ConvertFromString(hidden ? "#0F172A" : "#64748B")!;
        var description = hidden ? "Показать итоги" : "Скрыть итоги";
        ToggleTotalsButton.ToolTip = description;
        System.Windows.Automation.AutomationProperties.SetName(ToggleTotalsButton, description);
        _uiSettings.TotalsHidden = hidden;
    }

    private void LayoutTotalsCards()
    {
        if (TotalsCards is null) return;
        TotalsCards.Columns = ActualWidth >= 1180
            ? Math.Max(1, TotalsCards.Children.Cast<UIElement>().Count(card => card.Visibility == Visibility.Visible))
            : 2;
    }

    private void EstimateView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutTotalsCards();
    }

    private void EstimateView_Unloaded(object sender, RoutedEventArgs e) =>
        SaveEstimateUiSettings();

    /// <summary>
    /// Меню «Настройки → Показать подсказки снова» (reset_hints эталона):
    /// подсказки сметы и «Материалов» возвращаются с учётом активной вкладки.
    /// </summary>
    internal void ShowHintsAgain()
    {
        _uiSettings.EstimateHintHidden = false;
        _uiSettings.MaterialsHintHidden = false;
        _uiSettings.KsHintHidden = false;
        HintBar.Visibility = _visibleEstimateMode == "estimate"
            ? Visibility.Visible : Visibility.Collapsed;
        if (MaterialsHintBar is not null)
            MaterialsHintBar.Visibility = _visibleEstimateMode == "materials"
                ? Visibility.Visible : Visibility.Collapsed;
        ContractActsControl?.ShowHintsAgain();
        _uiSettings.Save();
    }


    private void RestoreEstimateUiSettings()
    {
        if (EstimateGrid is null) return;
        var columns = EstimateGrid.Columns.ToList();
        var savedWidths = _uiSettings.ColumnWidths ?? new Dictionary<string, double>();
        foreach (var column in columns)
        {
            var key = ColumnPreferenceKey(column);
            if (savedWidths.TryGetValue(key, out var width) &&
                double.IsFinite(width) && width >= 35 && width <= 1600)
                column.Width = new DataGridLength(width);
        }
        ApplyEstimateOrder(_uiSettings.ColumnOrder ?? new List<string>());
        var hidden = (_uiSettings.HiddenColumns ?? new List<string>())
            .ToHashSet(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column == PurchaseColumn || column == WorkColumn ||
                column == WorkTotalColumn || column == AreaUnitColumn ||
                column == AreaTotalColumn || column == BasisColumn ||
                column == NumberColumn || column == NameColumn) continue;
            column.Visibility = hidden.Contains(column.Header?.ToString() ?? "")
                ? Visibility.Collapsed : Visibility.Visible;
        }
        PurchaseToggle.IsChecked = _uiSettings.PurchaseVisible;
        PurchaseColumn.Visibility = _uiSettings.PurchaseVisible ? Visibility.Visible : Visibility.Collapsed;
        SetTotalsHidden(_uiSettings.TotalsHidden);
    }

    private void SaveEstimateUiSettings()
    {
        if (!_uiSettingsReady || EstimateGrid is null || TotalsCards is null) return;
        var columns = EstimateGrid.Columns.Where(c => !_inlineStageColumns.Contains(c)).ToList();
        // ColumnWidths contains preferred user widths, not the transient auto-fit
        // pixels. Header resize completion records preferences separately.
        RequestEstimateColumnFit();
        _uiSettings.ColumnOrder = columns.OrderBy(c => c.DisplayIndex)
            .Select(ColumnPreferenceKey).ToList();
        _uiSettings.HiddenColumns = columns.Where(c => c != PurchaseColumn &&
                c != WorkColumn && c != WorkTotalColumn && c != BasisColumn &&
                c.Visibility != Visibility.Visible)
            .Select(c => c.Header?.ToString() ?? "").ToList();
        _uiSettings.PurchaseVisible = PurchaseToggle.IsChecked == true;
        _uiSettings.AreaUnitVisible = AreaUnitColumn.Visibility == Visibility.Visible;
        _uiSettings.AreaTotalVisible = AreaTotalColumn.Visibility == Visibility.Visible;
        if (_estimate?.Form != "r4") _uiSettings.BasisVisible = BasisColumn.Visibility == Visibility.Visible;
        _uiSettings.TotalsHidden = TotalsCards.Visibility != Visibility.Visible;
        _uiSettings.Save();
    }

    private static string N2(decimal v) => v.ToString("N2", Ru);
    private static string Rate(decimal v) => v.ToString("0.##", Ru) + "%";

    private static string CountForm(int count, string singular, string few, string many) =>
        count % 100 is >= 11 and <= 14 ? many : (count % 10) switch
        {
            1 => singular,
            2 or 3 or 4 => few,
            _ => many
        };
}
