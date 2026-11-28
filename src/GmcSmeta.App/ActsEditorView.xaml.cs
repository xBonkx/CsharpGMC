using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>
/// Редактор акта КС-2 (ks_view.py): шапка в одну строку, панель действий, грид
/// строк с вводом «как в Excel», плитки итогов. Все записи — через транзакционные
/// методы SqlServerActs (SetQtyWithFormula / SetQuantitiesBatch / SaveDraft /
/// ToggleSigned / Create / Delete / AddExtraLine / UpdateExtraLine / DeleteExtraLine).
/// </summary>
public partial class ActsEditorView : UserControl
{
    private string _connectionString = "";
    private int _contractId;
    private string _title = "";
    private string _revision = "";
    private long _actId;
    private bool _building;
    private bool _loadingAct;
    private bool _loadFailed;
    private long _actLoadGeneration;
    private CancellationTokenSource? _actLoadCancellation;
    private string _actsRevision = "";
    private string _publishedActRevision = "";
    private long _publishedActId;
    private bool _tilesVisible = true;
    private List<SqlServerActs.ActInfo> _acts = new();
    private SqlServerActs.ActSheetData? _sheet;
    private SqlServerActs.ActDetails? _details;
    private ContractTotals? _totals;
    private bool _signed;
    private bool _contractLocked;
    private bool _isPosSmr;
    private bool _dsPerAct;
    private bool _roundRows;
    private bool _numManual;
    private bool _vatTop;
    private decimal _vatRate;
    private string _form = "";
    private bool _isContract;
    private TextBox? _qtyEditor;
    private int _moveAfter;
    private bool _inSave;
    private bool _signingAct;
    private bool _exportInProgress;
    private long _exportAttachmentGeneration;
    private int _pendingActWrites;
    private bool ActWritePending => _inSave || _signingAct || _pendingActWrites > 0;
    internal bool HasCacheActivity => ActWritePending || _loadingAct || _exportInProgress ||
        (ActGrid.ItemsSource is System.ComponentModel.IEditableCollectionView { IsEditingItem: true });

    /// <summary>Акт сменился или его итоги обновились (список актов слева перечитывает суммы).</summary>
    public event EventHandler? ActsChanged;
    /// <summary>«Акты» — свернуть список актов слева (владелец показывает/прячет панель).</summary>
    public event EventHandler<bool>? ListToggleChanged;
    /// <summary>«Компактно» — спрятать шапку вкладки.</summary>
    public event EventHandler<bool>? CompactChanged;
    public event EventHandler<string>? StatusRequested;

    public long SelectedActId => _resumeActId ?? (_loadingAct ? _activeReadActId : _actId);
    public IReadOnlyList<SqlServerActs.ActInfo> ActsSnapshot => _acts;
    public bool HasActs => _acts.Count > 0;

    public ActsEditorView()
    {
        InitializeComponent();
        InitializeActMenuIcons();
        ApplyTilesVisibility();
        Unloaded += (_,_) =>
        {
            _exportAttachmentGeneration++;
            _publishedActRevision = "";
            DetachQuantityEditor();
            CancelHiddenActRead();
        };
        Loaded += (_, _) => UpdateActReadVisibility();
        IsVisibleChanged += (_, _) => UpdateActReadVisibility();
    }

    /// <summary>Bind the same editor to a document, whether hosted inline or in a legacy window.</summary>
    public void Bind(string connectionString, int contractId, string title, long? selectedActId = null)
    {
        if (ActWritePending || _actLoadRequired) return;
        if (_contractId != contractId || _connectionString != connectionString)
        {
            _actExportCancellation?.Cancel();
            _actLoadCancellation?.Cancel();
            _actReadContext = null;
            _resumeActId = null;
            selectedActId ??= 0; // A pending ID from another document must never carry over.
            _actsRevision = "";
            _actId = 0;
            _acts = new();
            _sheet = null;
            _details = null;
            _totals = null;
        }
        _connectionString = connectionString;
        _contractId = contractId;
        _title = title;
        _revision = "";
        _ = ReloadAsync(selectedActId, reuseList: false, requiredReadback: false);
    }

    public void SelectAct(long actId)
    {
        if (_contractId == 0 || _building || _actLoadRequired) return;
        if (actId == SelectedActId && (_loadingAct || _resumeActId.HasValue ||
            (_sheet is not null && !string.IsNullOrEmpty(_revision)))) return;
        if (ActWritePending ||
            (!_loadingAct && !_resumeActId.HasValue && _sheet is not null &&
             (!ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
              !ActGrid.CommitEdit(DataGridEditingUnit.Row, true))) ||
            ActWritePending)
        {
            SetStatus("Завершите ввод и сохранение текущей КС-2 перед переключением.", true);
            SyncActSelector();
            ActsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _revision = "";
        _ = ReloadAsync(actId, reuseList: true, requiredReadback: false);
    }

    private void SyncActSelector()
    {
        var previous = _building;
        _building = true;
        try
        {
            var choices = _acts.Select(a => new ActItem(a)).ToList();
            if (ActCombo.ItemsSource is not List<ActItem> existing || !existing.SequenceEqual(choices))
                ActCombo.ItemsSource = choices;
            ActCombo.SelectedItem = (ActCombo.ItemsSource as List<ActItem>)?.FirstOrDefault(a => a.Info.Id == _actId);
        }
        finally { _building = previous; }
    }

    // Prevent a selection change from redirecting a pending write to another act.
    private async Task<T> RunActWriteAsync<T>(Func<T> action)
    {
        _pendingActWrites++;
        try { return await Task.Run(action); }
        finally { _pendingActWrites--; }
    }

    public void CreateAct() => NewAct_Click(this, new RoutedEventArgs());

    public void RefreshFromServer()
    {
        if (_contractId == 0 || _building || ActWritePending || _actLoadRequired) return;
        if (_sheet is not null && (!ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Row, true) || ActWritePending)) return;
        _revision = string.Empty;
        _ = ReloadAsync(null, reuseList: false, requiredReadback: false);
    }

    internal static bool CanReuseActOnReturn(string published, string displayed, string listRevision,
        string observed, long publishedId, long selectedId, bool cleanReadOnly) =>
        KsReadRevisionPolicy.CanReuse(published, displayed, listRevision, observed,
            publishedId, selectedId, cleanReadOnly);

    // Drafts retain the existing full readback: charges and quantity bindings
    // can contain unsaved input. Only signed acts and a confirmed empty list
    // have no such mutable fields.
    private bool CleanReadOnlyAct => !_loadFailed && !ActWritePending && !_exportInProgress &&
        (ActGrid.ItemsSource is not System.ComponentModel.IEditableCollectionView { IsEditingItem: true }) &&
        (_actId == 0 ? _acts.Count == 0 && _sheet is null :
            Current is { Signed: true } && _signed && _sheet is not null &&
            _details is not null && _totals is not null && SignedCheck.IsChecked == true);

    internal async void RefreshOnTabReturn()
    {
        if (_contractId == 0 || _building || _loadingAct || ActWritePending) return;
        if (_resumeActId.HasValue)
        {
            ResumeActReadIfNeeded();
            return;
        }
        if (IsLoaded && !IsVisible)
        {
            _ = ReloadAsync(null, reuseList: false, requiredReadback: false);
            return;
        }
        var revision = _revision;
        var actId = _actId;
        if (!IsLoaded || !CanReuseActOnReturn(_publishedActRevision, revision, _actsRevision,
            revision, _publishedActId, actId, CleanReadOnlyAct))
        {
            RefreshFromServer();
            return;
        }

        var generation = ++_actLoadGeneration;
        using var cancellation = new CancellationTokenSource();
        _actLoadCancellation = cancellation;
        _activeReadActId = actId;
        var token = cancellation.Token;
        var attachment = _exportAttachmentGeneration;
        var connectionString = _connectionString;
        var contractId = _contractId;
        _revision = "";
        _loadingAct = true;
        _building = true;
        // The signed checkbox, new-act command and selection must not run
        // during revalidation; existing write guards also reject empty revision.
        var enabled = IsEnabled;
        IsEnabled = false;
        var hit = false;
        try
        {
            var observed = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.OpenAsync(connectionString, token).GetAwaiter().GetResult();
                return SqlServerDatabase.RevisionAsync(conn, token).GetAwaiter().GetResult();
            }, token);
            if (token.IsCancellationRequested || generation != _actLoadGeneration || contractId != _contractId ||
                connectionString != _connectionString) return;
            hit = IsLoaded && IsVisible && attachment == _exportAttachmentGeneration &&
                CanReuseActOnReturn(_publishedActRevision, revision, _actsRevision,
                    observed, _publishedActId, _actId, CleanReadOnlyAct) && _actId == actId;
            if (hit)
            {
                _revision = revision;
                StartupTrace.Mark("act_tab_revision_hit");
            }
        }
        catch (Exception)
        {
            // A failed probe must not authorize writes. Fall back to the
            // established full loader and its error presentation.
        }
        finally
        {
            IsEnabled = enabled;
            if (ReferenceEquals(_actLoadCancellation, cancellation)) _actLoadCancellation = null;
            if (generation == _actLoadGeneration)
            {
                _loadingAct = false;
                _building = false;
                // A cancelled probe can return directly from the try block.
                // Resume here, even if its continuation never reaches the code below.
                if (token.IsCancellationRequested) ResumeActReadIfNeeded();
            }
        }
        if (generation != _actLoadGeneration || token.IsCancellationRequested || !IsLoaded || !IsVisible) return;
        if (!hit) await ReloadAsync(actId, reuseList: false, requiredReadback: false);
    }

    /// <summary>Показывать поле выбора акта (в отдельном окне без списка слева).</summary>
    public void ShowActSelector(bool show) =>
        ActCombo.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Кнопка «Акты» нужна только когда список актов вообще есть.</summary>
    public void ShowListToggle(bool show) =>
        ListToggle.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

    private string ServerRevision()
    {
        if (_loadingAct || _loadFailed || string.IsNullOrEmpty(_revision))
            throw new InvalidOperationException("Сначала перечитайте КС-2. Ревизия отображённого акта не подтверждена.");
        return _revision;
    }

    private sealed record ActCommandTarget(string ConnectionString, int ContractId, long ActId, string Revision);

    private ActCommandTarget? CaptureActCommand()
    {
        if (!IsLoaded || _building || ActWritePending || _loadingAct || _loadFailed ||
            _actId <= 0 || string.IsNullOrEmpty(_revision)) return null;
        return new ActCommandTarget(_connectionString, _contractId, _actId, _revision);
    }

    private bool IsCurrentActCommand(ActCommandTarget target) =>
        IsLoaded && !_building && !ActWritePending && !_loadingAct && !_loadFailed &&
        _connectionString == target.ConnectionString && _contractId == target.ContractId &&
        _actId == target.ActId && _revision == target.Revision;

    private void RequireCurrentActCommand(ActCommandTarget target)
    {
        if (!IsCurrentActCommand(target))
            throw new InvalidOperationException("Акт или данные изменились. Откройте команду заново.");
    }

    private void SetStatus(string message, bool error = false)
    {
        if (IsVisible && Window.GetWindow(this) is MainWindow main)
        {
            main.StatusText.Text = message;
            main.StatusText.Foreground = new SolidColorBrush(error
                ? Color.FromRgb(0xC5, 0x30, 0x30)
                : Color.FromRgb(0x64, 0x74, 0x8B));
        }
        StatusRequested?.Invoke(this, message);
    }

    private static bool Confirm(string title, string text) =>
        MessageBox.Show(Window.GetWindow(App.Current.MainWindow) ?? App.Current.MainWindow, text, title,
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    // ------------------------------------------------------------------ load

    public void Reload() => Reload(null);

    private void Reload(long? requestedActId) => _ = ReloadAsync(requestedActId, reuseList: false);

    private sealed record ActItem(SqlServerActs.ActInfo Info)
    {
        public override string ToString() =>
            $"КС-2 № {Info.Number} от {Info.Date:dd.MM.yyyy}" + (Info.Signed ? " — подписан" : "");
    }

    // Never consult ActCombo.SelectedItem here: it still holds the old act
    // when the left-hand list requests another ID, and it may be hidden.
    private SqlServerActs.ActInfo? Current => _acts.FirstOrDefault(a => a.Id == _actId);

    private void ApplyActSnapshot(ActLoadSnapshot loaded)
    {
            var current = Current;
            SyncActSelector();
            if (current is null)
            {
                ShowActUnavailable(null);
                return;
            }
            _sheet = loaded.Sheet ?? throw new InvalidOperationException("Нет строк КС-2.");
            _sharedExtraIds = loaded.SharedExtraIds;
            _details = loaded.Details;
            _totals = loaded.Totals;
            var rules = loaded.Rules ?? throw new InvalidOperationException("Нет настроек КС-2.");
            _form = _sheet.Form;
            _numManual = _sheet.NumManual;
            _dsPerAct = rules.Has(_form, "ds_per_act");
            _roundRows = rules.Has(_form, "round_rows");
            _vatTop = rules.Has(_form, "vat_top") || _form == "dns";
            _vatRate = _totals?.VatRate ?? 0m;
            _isPosSmr = _details?.SmrMode == "pos";
            _contractLocked = (_details?.ContractLocked ?? false) ||
                SqlSchemaCompatibility.IsPreviewDatabase(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(_connectionString).InitialCatalog);
            _isContract = _totals is not null && _totals.Kind is "main" or "dop";
            _signed = current.Signed;

            ActCaption.Text = $"КС-2 № {current.Number}" +
                (current.Date == default ? "" : $" от {current.Date:dd.MM.yyyy}");
            var bits = new List<string>();
            if (current.PeriodFrom is not null || current.PeriodTo is not null)
                bits.Add($"период {(current.PeriodFrom is DateTime f ? f.ToString("dd.MM.yyyy") : "…")} – " +
                         $"{(current.PeriodTo is DateTime t ? t.ToString("dd.MM.yyyy") : "…")}");
            if (_dsPerAct && !string.IsNullOrEmpty(current.DsNo))
                bits.Add($"ДС № {current.DsNo}" + (string.IsNullOrEmpty(current.DsDate) ? "" : $" от {current.DsDate}"));
            ActSub.Text = string.Join("   ·   ", bits);
            ActSub.Visibility = bits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ActCaption.ToolTip = bits.Count > 0 ? string.Join("   ·   ", bits) : "Двойной щелчок — свойства акта";

            // подписанный акт (или заблокированный договор) запрещает правки
            SignedCheck.IsChecked = _signed;
            PropsButton.IsEnabled = !_signed;
            ExtraMenuItem.IsEnabled = !_signed && !_contractLocked;
            DeleteActMenuItem.IsEnabled = !_signed && !_contractLocked;
            FillRestButton.IsEnabled = !_signed;
            ClearButton.IsEnabled = !_signed;
            IssuedMenuItem.IsEnabled = !_signed && !_contractLocked;
            var manualSections=!string.IsNullOrEmpty(_details?.SectionAmountsJson);
            SmrInput.IsEnabled = !_signed && !_contractLocked && !_isPosSmr && !manualSections;
            TrInput.IsEnabled = !_signed && !_contractLocked && !manualSections;
            PnrInput.IsEnabled = !_signed && !_contractLocked;
            SmrBySystemCheck.IsEnabled = !_signed && !_contractLocked && !manualSections;
            var adjustmentAvailable=SqlSchemaCompatibility.Schema31WritesAllowed(_connectionString)&&_details?.SupportsAdjustment==true;
            ActDiscountMenuItem.Visibility=SectionAmountsMenuItem.Visibility=adjustmentAvailable?Visibility.Visible:Visibility.Collapsed;
            ActDiscountMenuItem.IsEnabled=SectionAmountsMenuItem.IsEnabled=adjustmentAvailable&&!_signed&&!_contractLocked;
            RestAllMenuItem.IsEnabled=!_signed&&!_contractLocked&&!manualSections;

            BuildGrid();
            FillTiles();
            // The complete header, rows and totals are prepared before reveal.
            EmptyPanel.Visibility = Visibility.Collapsed;
            EditorRoot.Visibility = Visibility.Visible;
            SetStatus($"Открыта КС-2 № {current.Number}");
    }

    private void ClearActSheetState()
    {
        _sheet = null;
        _indexedRefNumbers = null;
        _quantityReferenceIndex = null;
        _sharedExtraIds.Clear();
        _details = null;
        _totals = null;
        DetachQuantityEditor();
        _rows = null;
        _referenceHighlights = null;
        _moveAfter = 0;
    }

    private void ShowActUnavailable(string? error)
    {
        ClearActSheetState();
        ActGrid.ItemsSource = null;
        EditorRoot.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyTitle.Text = error is null ? "Актов по этому документу ещё нет" : "Не удалось открыть КС-2";
        EmptyDescription.Text = error ?? "Нажмите «Новый акт», отметьте, что сдаёте, впишите СМР — и сохраните КС-2 и КС-3.";
        RetryActButton.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RetryAct_Click(object sender, RoutedEventArgs e) => RefreshFromServer();

    // ------------------------------------------------------------------ grid

    private static string Qty(decimal v) => v == decimal.Truncate(v)
        ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.###", CultureInfo.CurrentCulture);

    private decimal LineAmount(decimal qty, decimal price) => decimal.Round(qty * price, 2,
        _roundRows ? MidpointRounding.AwayFromZero : MidpointRounding.ToEven);

    private ObservableCollection<KsRowVm>? _rows;

    private void BuildGrid()
    {
        DetachQuantityEditor();
        var items = new ObservableCollection<KsRowVm>();
        var sheet = _sheet;
        if (sheet is not null)
        {
            var sections = ProjectActSections(sheet);
            var positionsBySystem = sheet.Positions.ToLookup(p => p.SystemId);
            foreach (var section in sections)
            {
                var back = section.IsSub ? "#E8F5FC" : "#F1F4F9";
                items.Add(new KsRowVm
                {
                    IsSection = true, IsSub = section.IsSub, SystemId = section.SystemId,
                    Name = section.Name, InAct = section.InAct,
                    Amount = SectionAmountText(section), RowBack = back, NowCellBack = back,
                });
                if (section.Unallocated) continue;
                foreach (var position in positionsBySystem[section.SystemId])
                    items.Add(PositionRow(position));
            }
        }
        _rows = items;
        _referenceHighlights = null;
        ActGrid.ItemsSource = items;
        FilterRows();
    }

    private KsRowVm PositionRow(SqlServerActs.ActSheetRow p)
    {
        var name = p.Name;
        var tooltip = p.Name;
        if (p.OutOfKp)
        {
            name += "   ·   Вне сметы";
            tooltip += "\nВне сметы";
        }
        if (_form == "r4" && !string.IsNullOrEmpty(p.WorkCode))
        {
            name += "   ·   " + p.WorkCode;
            tooltip += "\nКод работ: " + p.WorkCode;
        }
        var now = p.LineQty;
        var displayed = ActQuantityFormulas.Display(p.LineFormula ?? "", RefNumbers());
        var left = p.OutOfKp ? 0m : p.QtyByKp - p.ClosedByOthers - now;
        var inAct = p.LineId is not null;
        var warn = p.TrackIssues && p.ClosedByOthers + now - p.Issued > 0.000001m;
        var amount = inAct ? LineAmount(now, p.LinePrice) : 0m;
        var nowText = now != 0m || displayed.Length > 0 ? Qty(now) : "";
        return new KsRowVm
        {
            PositionId = p.PositionId,
            SystemId = p.SystemId,
            OutOfKp = p.OutOfKp,
            Num = p.NumText,
            Name = name,
            NameToolTip = tooltip,
            Unit = p.Unit,
            QtyByKp = p.OutOfKp ? "" : Qty(p.QtyByKp),
            Before = p.ClosedByOthers == 0m ? "" : Qty(p.ClosedByOthers),
            Now = nowText,
            NowValue = now,
            NowRaw = displayed.Length > 0 ? displayed : nowText,
            NowToolTip = displayed,
            Left = p.OutOfKp ? "" : (Math.Abs(left) > 0.000000001m ? Qty(left) : "—"),
            LeftRaw = left,
            RestRaw = p.OutOfKp ? 0m : p.QtyByKp - p.ClosedByOthers,
            Issued = p.Issued == 0m ? "" : Qty(p.Issued),
            Price = Money.Format(inAct ? p.LinePrice : p.Price),
            Amount = inAct ? Money.Format(amount) : "",
            InAct = inAct,
            Warn = warn,
            OverLeft = left < -0.000000001m,
        };
    }

    private void FilterRows()
    {
        if (_rows is null) return;
        var view = CollectionViewSource.GetDefaultView(ActGrid.ItemsSource);
        if (view is null) return;
        var words = SearchBox.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var onlyAct = OnlyActToggle.IsChecked == true;
        var rest = OnlyRestToggle.IsChecked == true;
        view.Filter = o => o is KsRowVm r &&
            r.MatchesSearch(words) &&
            (!onlyAct || r.InAct) &&
            (!rest || r.InAct || r.RestRaw > 0.000001m);
        UpdateSelectionQty();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        FilterRows();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (SearchHint is not null)
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_building) return;
        FilterRows();
    }

    private void ListToggle_Changed(object sender, RoutedEventArgs e) =>
        ListToggleChanged?.Invoke(this, ListToggle.IsChecked == true);

    private void Compact_Changed(object sender, RoutedEventArgs e)
    {
        var compact = CompactToggle.IsChecked == true;
        CompactToggle.Content = compact ? "Развернуть меню" : "Компактно";
        ActSub.Visibility = !compact && ActSub.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        CompactChanged?.Invoke(this, compact);
    }

    private void Tiles_Changed(object sender, RoutedEventArgs e)
    {
        _tilesVisible = TilesToggle.IsChecked == true;
        ApplyTilesVisibility();
    }

    private void ApplyTilesVisibility()
    {
        // IsChecked="True" в XAML поднимает Checked ещё до создания TotLine
        if (TilesPanel is null || TotLine is null) return;
        TilesPanel.Visibility = _tilesVisible ? Visibility.Visible : Visibility.Collapsed;
        TotLine.Visibility = !_tilesVisible && TotLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ActCaption_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) Props_Click(sender, new RoutedEventArgs());
    }

    // -------------------------------------------------------------- итоги акта

    private void TilesPanel_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) LayoutActTiles(e.NewSize.Width);
    }

    private void LayoutActTiles(double width)
    {
        if (TilesPanel is null || width <= 0 || double.IsInfinity(width)) return;
        var visible = TilesPanel.Children.Cast<UIElement>().Count(card => card.Visibility == Visibility.Visible);
        // Prefer a single row as in Python. On a narrow window allow a second row
        // rather than clipping the money or extending the window horizontally.
        var columns = Math.Max(1, Math.Min(visible, (int)(width / 150)));
        if (TilesPanel.Columns != columns) TilesPanel.Columns = columns;
    }

    private void FillTiles()
    {
        var current = Current;
        var totals = _totals;
        if (current is null || totals is null) return;
        MatValue.Text = Money.Format(current.Materials);
        SmrInput.Text = current.Smr == 0m ? "0" : Money.Format(current.Smr);
        var otherSmr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Smr);
        SmrHint.Text = (_isPosSmr ? "работа закрытых строк · " : "") +
                       $"по договору СМР {Money.Format(totals.Smr)}, останется {Money.Format(totals.Smr - otherSmr)}";
        var otherTr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Transport);
        TrTile.Visibility = totals.Transport != 0m || current.Transport != 0m
            ? Visibility.Visible : Visibility.Collapsed;
        TrInput.Text = current.Transport == 0m ? "0" : Money.Format(current.Transport);
        TrHint.Text = $"останется {Money.Format(totals.Transport - otherTr)}";
        var otherPnr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Pnr);
        PnrTile.Visibility = totals.Pnr != 0m || current.Pnr != 0m
            ? Visibility.Visible : Visibility.Collapsed;
        LayoutActTiles(TilesPanel.ActualWidth);
        PnrInput.Text = current.Pnr == 0m ? "0" : Money.Format(current.Pnr);
        PnrHint.Text = $"по договору {Money.Format(totals.Pnr)}, останется {Money.Format(totals.Pnr - otherPnr)}";
        if (_details is not null && SmrBySystemCheck.IsChecked != _details.SmrBySystem)
            SmrBySystemCheck.IsChecked = _details.SmrBySystem;
        TotalValue.Text = Money.Format(current.Total);
        var beforeDiscount = current.Materials + current.Smr + current.Transport + current.Pnr
            + (current.VatOnTop ? current.Vat + current.GrossAdjustmentCents / 100m : 0m);
        var adjustment = Money.Round2(beforeDiscount - current.Total);
        TotalValue.ToolTip = adjustment == 0m ? null :
            $"До скидки {Money.Format(beforeDiscount)} ₽; " +
            (adjustment > 0m ? "скидка " : "доплата ") + Money.Format(Math.Abs(adjustment)) + " ₽" +
            (current.ManualTotalCents >= 0 ? " · ручной итог" : "");
        TotalCaption.Text = _vatTop
            ? $"ИТОГО по акту с НДС  ·  без НДС {Money.Format(current.Ks3Net)} + НДС {FormatRate(_vatRate)}% {Money.Format(current.Vat)}"
            : $"ИТОГО по акту с НДС  ·  в т.ч. НДС {FormatRate(_vatRate)}%: {Money.Format(current.Vat)}";

        // прогресс по договору (acts.contract_progress): «Сдано по КС» = только подписанные
        var signed = _acts.Where(a => a.Signed).Sum(a => a.Total);
        var pending = _acts.Where(a => !a.Signed).Sum(a => a.Total);
        var total = totals.Total;
        var pct = total == 0m ? 0m : decimal.Round(signed / total * 100m, 0, MidpointRounding.ToEven);
        var left = total - signed - pending;
        DoneValue.Text = $"{pct:0}%  ·  остаток {Money.Format(left)}";
        DoneCaption.Text = "Сдано по договору (подписано)" +
            (pending != 0m ? $"  ·  на сдаче {Money.Format(pending)}" : "");
        TotLine.Inlines.Clear();
        TotLine.Inlines.Add(
            $"Материалы в акте {Money.Format(current.Materials)}   ·   СМР {Money.Format(current.Smr)}   ·   ");
        TotLine.Inlines.Add(new System.Windows.Documents.Run(
            $"ИТОГО по акту с НДС {Money.Format(current.Total)} ₽") { FontWeight = FontWeights.Bold });
        TotLine.Inlines.Add($"   ·   подписано {pct:0}%" +
            (pending != 0m ? $", на сдаче {Money.Format(pending)}" : "") +
            $", остаток {Money.Format(left)}");
        TotLine.Visibility = !_tilesVisible && TotLine.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string FormatRate(decimal rate) =>
        rate == decimal.Truncate(rate) ? rate.ToString("0") : rate.ToString("0.##", CultureInfo.CurrentCulture);

    // ------------------------------------------------------------ ввод объёма

    private void ActGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (_building || _loadingAct || _loadFailed || string.IsNullOrEmpty(_revision) ||
            ActWritePending || e.Row.Item is not KsRowVm row || e.Column.Header?.ToString() != "В этом акте"
            || _signed || _contractLocked || row.IsSection)
        {
            e.Cancel = true;
        }
    }

    private void ActGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        if (e.EditingElement is not TextBox editor) return;
        DetachQuantityEditor();
        _qtyEditor = editor;
        var raw = ((KsRowVm)e.Row.Item).NowRaw;
        editor.Text = !raw.StartsWith('=') &&
            QuantityReferenceIndex().EditingReferences("="+raw).Count>0 ? "="+raw : raw;
        editor.TextChanged += QuantityEditor_TextChanged;
        editor.Unloaded += QuantityEditor_Unloaded;
        UpdateQuantityReferences();
        editor.SelectAll();
        editor.ToolTip = "Начните с = и щёлкайте по ячейкам «В этом акте». Enter — сохранить, Esc — отменить.";
        editor.KeyDown -= QtyEditor_KeyDown;
        editor.KeyDown += QtyEditor_KeyDown;
    }

    private void QtyEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up or Key.Enter or Key.Return)
        {
            _moveAfter = e.Key == Key.Up ? -1 : 1;
            e.Handled = true;
            ActGrid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private async void ActGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel)
        {
            DetachQuantityEditor();
            _moveAfter=0;
            return;
        }
        if (_building) return;
        if (ActWritePending) { e.Cancel = true; return; }
        if (e.Row.Item is not KsRowVm row || e.Column.Header?.ToString() != "В этом акте"
            || e.EditingElement is not TextBox editor) return;
        if (_signed || _contractLocked) return;
        var step = _moveAfter;
        _moveAfter = 0;
        GmcSmeta.Core.Domain.ActQuantityInput.Parsed input;
        try
        {
            input = ActQuantityInput.Parse(editor.Text, RefNumbers());
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
            e.Cancel = true;
            return;
        }
        DetachQuantityEditor();
        await SaveQtyAsync(row.PositionId, input.Quantity, input.Formula, step,
            positionName: row.Name);
    }

    /// <summary>Сохранить объём (acts.set_qty) и обновить ОДНУ строку + итоги, не пересобирая грид.
    /// «Сверх КП» — подтверждение как в Python (_confirm_over).</summary>
    private async Task SaveQtyAsync(long positionId, decimal qty, string formula, int step,
        bool allowOver = false, string? positionName = null)
    {
        var advance = false;
        _inSave = true;
        try
        {
            var saved = await RunActWriteAsync(() => SqlServerActs.SetQtyWithFormula(
                _connectionString, _actId, positionId, qty, formula, ServerRevision(), allowOver));
            if (!saved.Saved)
            {
                if (!allowOver && (saved.Error ?? "").StartsWith("Сверх КП"))
                {
                    var remaining = RemainingOf(positionId);
                    if (Confirm("Больше, чем по смете",
                            $"{positionName ?? ""}\nПо смете осталось {Qty(Math.Max(0m, remaining))}. " +
                            $"Вписать {Qty(qty)}?"))
                    {
                        _inSave = false;
                        await SaveQtyAsync(positionId, qty, formula, step, allowOver: true,
                            positionName: positionName);
                        return;
                    }
                }
                SetStatus("Ошибка: " + saved.Error, true);
                _revision = "";
                return;
            }
            _revision = saved.Revision;
            SetStatus(formula.Contains('@') ? "Формула объёма применена." : "Количество сохранено.");
            advance = await RefreshChangedRowsAsync(positionId) && step != 0;
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Ошибка: " + ex.Message, true);
        }
        finally
        {
            _inSave = false;
        }
        // BeginningEdit refuses concurrent writes. Advance only after the
        // readback and save guard have both finished.
        if (advance) StepRow(positionId, step);
    }

    private decimal RemainingOf(long positionId)
    {
        var p = _sheet?.Positions.FirstOrDefault(x => x.PositionId == positionId);
        return p is null ? 0m : p.QtyByKp - p.ClosedByOthers;
    }

    /// <summary>Обновление одной строки (ks_view._refresh_rows): перечитать лист и поправить
    /// только изменившиеся ячейки, суммы разделов, плитки и список актов — без пересборки грида.</summary>
    private async Task<bool> RefreshChangedRowsAsync(long editedPositionId)
    {
        var connectionString = _connectionString;
        var contractId = _contractId;
        var actId = _actId;
        var revision = _revision;
        var previousSheet = _sheet;
        ActLoadSnapshot loaded;
        IReadOnlyDictionary<long, SqlServerActs.ActSheetRow>? patch;
        try
        {
            (loaded, patch) = await Task.Run(() =>
            {
                var snapshot = ReadActSnapshot(connectionString, contractId, actId, null, "");
                var changes = snapshot.Sheet is null ? null :
                    KsActSheetProjection.QuantityPatch(previousSheet, snapshot.Sheet, editedPositionId);
                return (snapshot, changes);
            });
            if (loaded.ActId != actId || loaded.Revision != revision || loaded.Sheet is null ||
                loaded.Details is null || loaded.Totals is null)
                throw new InvalidOperationException("Данные изменились после записи. Перечитайте КС-2.");
        }
        catch (Exception ex)
        {
            _revision = "";
            _actsRevision = "";
            SetStatus("Ошибка обновления строки: " + ex.Message, true);
            return false;
        }
        if (_actId != actId || _contractId != contractId || _connectionString != connectionString ||
            _revision != revision) return false;
        var sheet = loaded.Sheet!;
        _sheet = sheet;
        _acts = loaded.Acts;
        _actsRevision = loaded.Revision;
        _details = loaded.Details;
        _totals = loaded.Totals;
        _sharedExtraIds = loaded.SharedExtraIds;
        RememberReadContext(loaded);
        var sections = ProjectActSections(sheet);
        var existingSections = _rows?.Where(r => r.IsSection).ToArray();
        if (patch is null || _rows is null || existingSections is null ||
            !existingSections.Select(r => r.SystemId).SequenceEqual(sections.Select(r => r.SystemId)))
        {
            // A quantity change can add/remove an extra row or allocation header.
            // Never retain an obsolete row under a newly confirmed revision.
            BuildGrid();
        }
        else
        {
            var filterChanged = false;
            foreach (var vm in _rows)
            {
                if (vm.IsSection)
                    continue;
                if (!patch.TryGetValue(vm.PositionId, out var p))
                    continue;
                var displayed = ActQuantityFormulas.Display(p.LineFormula ?? "", RefNumbers());
                var left = p.OutOfKp ? 0m : p.QtyByKp - p.ClosedByOthers - p.LineQty;
                var inAct = p.LineId is not null;
                var amount = inAct ? LineAmount(p.LineQty, p.LinePrice) : 0m;
                var nowText = p.LineQty != 0m || displayed.Length > 0 ? Qty(p.LineQty) : "";
                filterChanged |= vm.InAct != inAct ||
                    (vm.RestRaw > 0.000001m) != (!p.OutOfKp && p.QtyByKp - p.ClosedByOthers > 0.000001m);
                vm.Before = p.ClosedByOthers == 0m ? "" : Qty(p.ClosedByOthers);
                vm.Now = nowText;
                vm.NowValue = p.LineQty;
                vm.NowRaw = displayed.Length > 0 ? displayed : nowText;
                vm.NowToolTip = displayed;
                vm.Left = p.OutOfKp ? "" : (Math.Abs(left) > 0.000000001m ? Qty(left) : "—");
                vm.LeftRaw = left;
                vm.RestRaw = p.OutOfKp ? 0m : p.QtyByKp - p.ClosedByOthers;
                vm.Issued = p.Issued == 0m ? "" : Qty(p.Issued);
                vm.Price = Money.Format(inAct ? p.LinePrice : p.Price);
                vm.Amount = inAct ? Money.Format(amount) : "";
                vm.InAct = inAct;
                vm.Warn = p.TrackIssues && p.ClosedByOthers + p.LineQty - p.Issued > 0.000001m;
                vm.OverLeft = left < -0.000000001m;
            }
            for (var i = 0; i < sections.Count; i++)
            {
                var vm = existingSections[i];
                var section = sections[i];
                filterChanged |= vm.Name != section.Name || vm.InAct != section.InAct;
                vm.Name = section.Name;
                vm.InAct = section.InAct;
                vm.Amount = SectionAmountText(section);
            }
            if (filterChanged && (OnlyActToggle.IsChecked == true || OnlyRestToggle.IsChecked == true ||
                !string.IsNullOrWhiteSpace(SearchBox.Text)))
                FilterRows();
        }
        FillTiles();
        UpdateSelectionQty();
        ActsChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>↓ / Enter — к «В этом акте» следующей видимой строки, ↑ — к предыдущей.</summary>
    private void StepRow(long positionId, int step)
    {
        if (_rows is null) return;
        var view = CollectionViewSource.GetDefaultView(ActGrid.ItemsSource);
        Predicate<object>? filter = view?.Filter;
        KsRowVm? target = null;
        KsRowVm? previous = null;
        var found = false;
        foreach (var row in _rows)
        {
            if (row.IsSection || (filter is not null && !filter(row))) continue;
            if (found) { target = row; break; }
            if (row.PositionId == positionId)
            {
                if (step < 0) { target = previous; break; }
                found = true;
            }
            previous = row;
        }
        if (target is null) return;
        var column = ActGrid.Columns.FirstOrDefault(c => c.Header?.ToString() == "В этом акте");
        if (column is null) return;
        ActGrid.SelectedItem = target;
        ActGrid.ScrollIntoView(target);
        ActGrid.CurrentCell = new DataGridCellInfo(target, column);
        ActGrid.BeginEdit();
    }

    private void UpdateSelectionQty()
    {
        if (_rows is null || _building) return;
        var selected = ActGrid.SelectedItems?.OfType<KsRowVm>().Where(r => !r.IsSection).ToList();
        if (selected is null || selected.Count == 0)
        {
            SelectionQty.Text = "";
            SelectionQty.Visibility = Visibility.Collapsed;
            return;
        }
        var text = KsSelectionQuantity.Describe(selected.Select(r => (r.Unit, r.NowValue)),
            CultureInfo.CurrentCulture);
        SelectionQty.Text = text;
        SelectionQty.ToolTip = text;
        SelectionQty.Visibility = Visibility.Visible;
    }

    private void ActGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionQty();

    private void ActGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // правка позиции вне сметы: двойной щелчок по «Наименование» или «Цена»
        var header = ActGrid.CurrentCell.Column?.Header?.ToString();
        if (header is not ("Наименование" or "Цена")) return;
        if (ActGrid.SelectedItem is not KsRowVm row || !row.OutOfKp || !row.InAct) return;
        if (_signed || _contractLocked) return;
        var sheet = _sheet;
        bool RowCurrent() => ReferenceEquals(_sheet, sheet) &&
            ReferenceEquals(ActGrid.SelectedItem, row) && _rows?.Contains(row) == true;
        EditExtraDialog(row.PositionId, RowCurrent);
    }

    // -------------------------------------------------------------- контекстное меню

    private void ActGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var hit = ContextMenuTarget.SelectRow<KsRowVm>(ActGrid, e);
        if (_actId == 0 || _sheet is null || hit is null || _loadingAct || _loadFailed)
        {
            e.Handled = true;
            return;
        }
        var menu = new ContextMenu();
        var menuActId = _actId;
        var menuContractId = _contractId;
        var menuConnection = _connectionString;
        var menuSheet = _sheet;
        var menuRevision = _revision;
        var menuRows = ActGrid.SelectedItems.OfType<KsRowVm>().ToArray();
        bool CanRead() => IsLoaded && !_loadingAct && !_loadFailed &&
            !_building && !ActWritePending && _actId == menuActId &&
            _contractId == menuContractId && _connectionString == menuConnection &&
            ReferenceEquals(_sheet, menuSheet) &&
            !string.IsNullOrWhiteSpace(menuRevision) && _revision == menuRevision &&
            ActGrid.SelectedItems.OfType<KsRowVm>().SequenceEqual(menuRows);
        bool CanExecute() => CanRead() && !_signed && !_contractLocked;
        var rest = ContextMenuIcons.Create("Закрыть выделенное", "Check");
        rest.Click += (_, _) => { if (CanExecute()) FillRest_Click(sender, new RoutedEventArgs()); };
        var clear = ContextMenuIcons.Create("Убрать выделенное", "X", danger: true);
        clear.Click += (_, _) => { if (CanExecute()) ClearSelected_Click(sender, new RoutedEventArgs()); };
        var enabled = CanExecute() && menuRows.Length > 0;
        rest.IsEnabled = enabled;
        clear.IsEnabled = enabled;
        menu.Items.Add(rest);
        menu.Items.Add(clear);

        // Python ks_view._row_menu: ссылки хранят ID позиций, а не номер строки.
        var refs = RefNumbers();
        if (!hit.IsSection && refs.ContainsKey(hit.PositionId))
        {
            void AddCopy(string header, string value)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) =>
                {
                    if (!CanRead()) return;
                    try { Clipboard.SetText(value); }
                    catch (System.Runtime.InteropServices.ExternalException ex)
                    { SetStatus("Не удалось скопировать: " + ex.Message, true); }
                };
                menu.Items.Add(item);
            }
            AddCopy("Скопировать ссылку на объём", $"@{hit.PositionId}");
            var selectedRefs = ActGrid.SelectedItems.OfType<KsRowVm>()
                .Where(row => !row.IsSection && refs.ContainsKey(row.PositionId))
                .Select(row => row.PositionId).Distinct().ToArray();
            if (selectedRefs.Length > 1)
                AddCopy("Скопировать формулу суммы объёмов",
                    "=" + string.Join("+", selectedRefs.Select(id => $"@{id}")));
        }

        if (enabled && !hit.IsSection && hit.OutOfKp && hit.InAct)
        {
            menu.Items.Add(new Separator());
            var edit = ContextMenuIcons.Create("Изменить позицию вне сметы…", "Edit");
            edit.Click += (_, _) =>
            {
                if (CanExecute() && _rows?.Contains(hit) == true)
                    EditExtraDialog(hit.PositionId, CanExecute);
            };
            menu.Items.Add(edit);
            AddExtraMoveCommands(menu, hit, menuActId, menuRevision, CanExecute);
        }
        menu.PlacementTarget = ActGrid;
        menu.Placement = e.CursorLeft < 0 && e.CursorTop < 0
            ? System.Windows.Controls.Primitives.PlacementMode.Bottom
            : System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        e.Handled = true;
    }

    // -------------------------------------------------------------- заполнение акта

    private List<KsBatchPosition> SelectedPositionStates()
    {
        if (_sheet is null || _rows is null) return new List<KsBatchPosition>();
        static KsBatchSelectionRow Row(KsRowVm vm) => new(vm.IsSection, vm.SystemId, vm.PositionId);
        return KsBatchSelection.Resolve(
            _sheet.Positions.Select(p => new KsBatchPosition(p.PositionId, p.QtyByKp,
                p.ClosedByOthers, p.LineQty, p.Price, p.OutOfKp, (p.LineFormula ?? "").Contains('@'))),
            _rows.Select(Row),
            (ActGrid.SelectedItems?.OfType<KsRowVm>() ?? Enumerable.Empty<KsRowVm>()).Select(Row));
    }

    /// <summary>«Закрыть выделенное» (acts.fill mode=rest): вписать весь остаток выделенных строк.</summary>
    private async void FillRest_Click(object sender, RoutedEventArgs e)
    {
        var current = Current;
        if (current is null || current.Signed) return;
        var states = SelectedPositionStates();
        if (states.Count == 0)
        {
            SetStatus("Выделите строки или систему", true);
            return;
        }
        var edits = KsBatchQuantities.FillRemaining(states);
        if (edits.Count == 0)
        {
            SetStatus("Нечего добавлять: всё уже сдано или не выдано");
            return;
        }
        await SaveBatchAsync(edits, $"В акт вписано строк: {edits.Count}");
    }

    /// <summary>«Убрать выделенное» (acts.clear): удалить выделенные строки из акта.</summary>
    private async void ClearSelected_Click(object sender, RoutedEventArgs e)
    {
        var current = Current;
        if (current is null || current.Signed) return;
        var states = SelectedPositionStates();
        var edits = KsBatchQuantities.Clear(states);
        if (edits.Count == 0)
        {
            SetStatus("В выделенных строках нет объёмов");
            return;
        }
        await SaveBatchAsync(edits, $"Убрано строк: {edits.Count}");
    }

    /// <summary>«Сдать то, что выдано на объект» (acts.fill mode=issued).</summary>
    private async void FillIssued_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureMoreCommand(sender, selectionRequired: true) is null || _signed || _contractLocked) return;
        var current = Current;
        if (current is null || current.Signed) return;
        var states = SelectedPositionStates();
        if (states.Count == 0)
        {
            SetStatus("Выделите строки или систему", true);
            return;
        }
        var issued = _sheet?.Positions.ToDictionary(p => p.PositionId, p => p.Issued)
                     ?? new Dictionary<long, decimal>();
        var edits = KsBatchQuantities.FillIssued(states, issued);
        if (edits.Count == 0)
        {
            SetStatus("Нечего добавлять: всё уже сдано или не выдано");
            return;
        }
        await SaveBatchAsync(edits, $"В акт вписано строк: {edits.Count}");
    }

    /// <summary>«Сдать весь остаток по договору (закрыть на 100 %)…» (acts.fill_all_rest).</summary>
    private async void FillAllRest_Click(object sender, RoutedEventArgs e)
    {
        var target = CaptureMoreCommand(sender);
        if (target is null || !string.IsNullOrEmpty(_details?.SectionAmountsJson)) return;
        var sheet = _sheet;
        var current = Current;
        var totals = _totals;
        if (ActWritePending || _building || current is null || current.Signed || _contractLocked ||
            totals is null || _sheet is null || string.IsNullOrEmpty(_revision)) return;
        var revision = _revision;
        var actId = current.Id;
        var contractId = _contractId;
        var linked = _sheet.Positions
            .Where(p => (p.LineFormula ?? "").Contains('@'))
            .Select(p => p.PositionId).ToHashSet();
        var states = _sheet.Positions
            .Where(p => !p.OutOfKp && !linked.Contains(p.PositionId))
            .Select(p => (p.PositionId, Qty: ActQuantityPolicy.Remaining(p.QtyByKp, p.ClosedByOthers),
                p.LineQty, p.Price))
            .Where(s => Math.Abs(s.Qty - s.LineQty) > 0.000001m)
            .ToList();
        var restSum = states.Sum(s => s.Qty * s.Price);
        if (!Confirm("Сдать весь остаток",
                $"Вписать в КС-2 №{current.Number} несданный остаток по договору?\n\n" +
                $"Строк с остатком: {states.Count}, примерно {Money.Format(restSum)} ₽ по ценам сметы " +
                "(+ СМР, транспорт и ПНР, если остались)." +
                (linked.Count > 0 ? "\nСтроки со ссылками пересчитаются по своим формулам." : "")))
            return;
        if (!IsCurrentActCommand(target) || !ReferenceEquals(sheet, _sheet) || _signed || _contractLocked ||
            !string.IsNullOrEmpty(_details?.SectionAmountsJson) || _actId != actId ||
            _contractId != contractId || _revision != revision)
        {
            SetStatus("Данные или выбранная КС изменились. Повторите операцию.", true);
            return;
        }
        var edits = states.Select(s => (s.PositionId, s.Qty)).ToList();
        var otherSmr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Smr);
        var otherTr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Transport);
        var otherPnr = _acts.Where(a => a.Id != current.Id).Sum(a => a.Pnr);
        decimal? smr = _isPosSmr ? null : Math.Max(0m, totals.Smr - otherSmr);
        var tr = Math.Max(0m, totals.Transport - otherTr);
        var pnr = Math.Max(0m, totals.Pnr - otherPnr);
        await SaveBatchAndChargesAsync(edits, smr, tr, pnr,
            $"Остатки вписаны в акт: строк {edits.Count}");
    }

    /// <summary>«Закрыть разделы…» — галочки по системам, весь остаток раздела в акт.</summary>
    private async void CloseSections_Click(object sender, RoutedEventArgs e)
    {
        var current = Current;
        if (current is null || current.Signed || _sheet is null) return;
        var tops = _sheet.Systems.Where(s => !s.IsSub).ToList();
        var positionsByTop = new Dictionary<long, List<long>>();
        var restByTop = new Dictionary<long, decimal>();
        foreach (var top in tops)
        {
            var systemIds = new List<long> { top.SystemId };
            systemIds.AddRange(_sheet.Systems
                .Where(s => s.IsSub && s.ParentId == top.SystemId)
                .Select(s => s.SystemId));
            var positions = _sheet.Positions
                .Where(p => systemIds.Contains(p.SystemId) && !p.OutOfKp)
                .ToList();
            positionsByTop[top.SystemId] = positions.Select(p => p.PositionId).ToList();
            restByTop[top.SystemId] = positions
                .Sum(p => ActQuantityPolicy.Remaining(p.QtyByKp, p.ClosedByOthers) * p.Price);
        }

        var chosenTops = KsCloseSectionsDialog.Show(this, current.Number,
            tops.Select(top => (top.SystemId, top.Label, restByTop.GetValueOrDefault(top.SystemId))));
        if (chosenTops is null) return;
        if (chosenTops.Count == 0)
            return;
        var ids = chosenTops.SelectMany(t => positionsByTop.GetValueOrDefault(t) ?? new List<long>()).ToHashSet();
        var byId = _sheet.Positions.ToDictionary(p => p.PositionId);
        var edits = ids
            .Select(id => (id, Qty: ActQuantityPolicy.Remaining(byId[id].QtyByKp, byId[id].ClosedByOthers)))
            .Where(s => Math.Abs(s.Qty - byId[s.id].LineQty) > 0.000001m)
            .Select(s => (s.id, s.Qty))
            .ToList();
        if (edits.Count == 0)
        {
            SetStatus("Нечего добавлять: всё уже сдано или не выдано");
            return;
        }
        await SaveBatchAsync(edits, $"Закрыто разделов: {chosenTops.Count}, вписано строк: {edits.Count}");
    }

    /// <summary>Пакет объёмов одной транзакцией (acts.apply_quantities).</summary>
    private async Task SaveBatchAsync(List<(long PositionId, decimal Qty)> edits, string doneMessage)
    {
        var target = CaptureActCommand();
        if (target is null || edits.Count == 0) return;
        SetStatus($"Сохранение {edits.Count} строк…");
        try
        {
            var saved = await RunActWriteAsync(() => SqlServerActs.SetQuantitiesBatch(
                target.ConnectionString, target.ActId, edits, target.Revision));
            if (!saved.Saved)
            {
                SetStatus("Ошибка: " + saved.Error, true);
                _revision = "";
                return;
            }
            _revision = saved.Revision;
            SetStatus(doneMessage);
            await ReloadActAfterSave();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Исход сохранения неизвестен: " + ex.Message + " Обновите КС перед новой правкой.", true);
        }
    }

    /// <summary>Пакет объёмов + суммы СМР/транспорта/ПНР одной операцией (fill_all_rest).</summary>
    private async Task SaveBatchAndChargesAsync(List<(long PositionId, decimal Qty)> edits,
        decimal? smr, decimal tr, decimal pnr, string doneMessage)
    {
        if (ActWritePending || !IsLoaded || string.IsNullOrEmpty(_revision)) return;
        var revision = _revision;
        var actId = _actId;
        var contractId = _contractId;
        var connectionString = _connectionString;
        var charges = new SqlServerActs.ActBatchCharges(smr, tr, pnr);
        SetStatus("Сохранение объёмов и сумм одной транзакцией…");
        try
        {
            var saved = await RunActWriteAsync(() => SqlServerActs.SetQuantitiesAndChargesBatch(
                connectionString, contractId, actId, edits, charges, revision));
            if (!saved.Saved)
            {
                _revision = "";
                SetStatus("Изменения не сохранены: " + saved.Error, true);
                return;
            }
            _revision = saved.Revision;
            SetStatus(doneMessage);
            await ReloadActAfterSave();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Сохранение не подтверждено: " + ex.Message + ". Обновите КС перед повтором.", true);
        }
    }

    /// <summary>После пакетных правок перечитать акт и пересобрать строки/плитки/список.</summary>
    private async Task ReloadActAfterSave()
    {
        // Structural or batch writes can change charges, extra ownership and
        // revisions. Publish a full coherent snapshot, not just lines.
        var actId = _actId;
        var contractId = _contractId;
        var connectionString = _connectionString;
        var selectedIds = ActGrid.SelectedItems?.OfType<KsRowVm>()
            .Where(r => !r.IsSection).Select(r => r.PositionId).ToHashSet();
        await ReloadAsync(actId, reuseList: false);
        if (_actId != actId || _contractId != contractId || _connectionString != connectionString ||
            _loadFailed || string.IsNullOrEmpty(_revision)) return;
        if (selectedIds is { Count: > 0 } && _rows is not null)
        {
            var restore = _rows.FirstOrDefault(r => !r.IsSection && selectedIds.Contains(r.PositionId));
            if (restore is not null)
            {
                ActGrid.SelectedItem = restore;
                ActGrid.ScrollIntoView(restore);
            }
        }
    }

    // -------------------------------------------------------------- подписан / удаление / новый

    private async void NewAct_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingAct || _loadFailed || ActWritePending || _contractId == 0)
        {
            SetStatus("Сначала завершите успешную загрузку или сохранение КС-2.", true);
            return;
        }
        try
        {
            _actId = await RunActWriteAsync(() => SqlServerActs.Create(
                _connectionString, _contractId, ServerRevision()));
            _revision = string.Empty;
            SetStatus("Новый акт создан — отметьте, что сдаёте");
            Reload();
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка: " + ex.Message, true);
        }
    }

    private async void DeleteAct_Click(object sender, RoutedEventArgs e)
    {
        if (CaptureMoreCommand(sender) is null) return;
        if (ActWritePending || !ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Row, true) || ActWritePending)
        { SetStatus("Сначала завершите сохранение текущего акта.", true); return; }
        var target = CaptureActCommand();
        var sheet = _sheet;
        var current = Current;
        if (target is null || current is null || sheet is null) return;
        if (current.Signed || _contractLocked)
        {
            MessageBox.Show(Window.GetWindow(this), "Акт подписан или договор заблокирован. Удаление недоступно.",
                "Удаление", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Confirm("Удаление", $"Удалить КС-2 № {current.Number} от {current.Date:dd.MM.yyyy}? " +
                "Объёмы вернутся в остаток. Позиции вне сметы без других ссылок будут удалены. " +
                "Восстановление акта не предусмотрено."))
            return;
        try
        {
            RequireCurrentActCommand(target);
            if (!ReferenceEquals(sheet, _sheet) || !Equals(Current, current) || _contractLocked || current.Signed)
                throw new InvalidOperationException("Снимок акта изменился. Повторите команду.");
            var saved = await RunActWriteAsync(() => SqlServerActs.Delete(
                target.ConnectionString, target.ActId, target.Revision, target.ContractId));
            if (_actId != target.ActId || _contractId != target.ContractId ||
                _connectionString != target.ConnectionString || !IsLoaded) return;
            if (!saved.Saved)
            {
                SetStatus("Ошибка удаления: " + saved.Error, true);
                _revision = "";
                Reload();
                return;
            }
            _revision = saved.Revision;
            _actId = 0;
            SetStatus("Акт удалён");
            Reload();
        }
        catch (Exception ex)
        {
            if (_actId == target.ActId && _contractId == target.ContractId &&
                _connectionString == target.ConnectionString)
            {
                _revision = "";
                SetStatus("Удаление не подтверждено: " + ex.Message + ". Не повторяйте до сверки.", true);
                if (IsLoaded) Reload();
            }
        }
    }

    private async void Signed_Changed(object sender, RoutedEventArgs e)
    {
        if (_building) return;
        var signed = SignedCheck.IsChecked == true;
        if (signed == _signed) return;
        if (ActWritePending)
        {
            RevertSignedCheck();
            SetStatus("Дождитесь завершения сохранения объёмов КС-2.", true);
            return;
        }
        var target = CaptureActCommand();
        var sheet = _sheet;
        if (target is null || sheet is null || _contractLocked) { RevertSignedCheck(); return; }
        var actId = target.ActId;
        bool SameAct() => _actId == actId && _contractId == target.ContractId &&
            _connectionString == target.ConnectionString;
        bool CurrentSigningTarget() => SameAct() && IsLoaded && !_loadingAct && !_loadFailed &&
            !_building && !_contractLocked && !_inSave && ReferenceEquals(sheet, _sheet) &&
            _revision == target.Revision;
        _signingAct = true;
        SignedCheck.IsEnabled = false;
        try
        {
            // A failed/invalid edit must not be bypassed by signing the last
            // successfully saved server value.
            if (!ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
                !ActGrid.CommitEdit(DataGridEditingUnit.Row, true))
            {
                SetStatus("Не удалось завершить ввод в ячейке. Исправьте значение перед подписанием.", true);
                return;
            }
            if (_inSave)
            {
                SetStatus("Дождитесь завершения записи объёмов КС-2.", true);
                return;
            }
            var expectedRevision = target.Revision;
            if (!CurrentSigningTarget()) return;
            if (signed)
            {
                // Audit fresh SQL data, not stale/optimistic WPF text.
                var audit = await ReadPreSignAuditAsync(actId, target.ContractId);
                if (!CurrentSigningTarget()) return;
                if (!string.Equals(audit.Revision, expectedRevision, StringComparison.Ordinal))
                {
                    _revision = "";
                    SetStatus("КС-2 изменилась на сервере. Обновите акт перед подписанием.", true);
                    return;
                }
                if (!Confirm("Проверка перед подписанием КС-2",
                        audit.Report.Format() +
                        "\n\nОтметить этот акт как подписанный и заблокировать его редактирование?"))
                    return;
            }
            else if (!Confirm("Акт подписан", "Снять отметку «Подписан» и разрешить изменения?"))
                return;

            if (!CurrentSigningTarget()) return;
            var saved = await RunActWriteAsync(() => SqlServerActs.ToggleSigned(
                target.ConnectionString, actId, signed, expectedRevision, target.ContractId));
            if (!SameAct() || !IsLoaded) return;
            if (!saved.Saved)
            {
                _revision = "";
                SetStatus("Не удалось изменить статус КС-2: " + saved.Error, true);
                return;
            }
            _revision = saved.Revision;
            SetStatus(signed ? "Акт отмечен как подписанный" : "Акт снова можно менять");
        }
        catch (Exception ex)
        {
            if (SameAct())
            {
                _revision = "";
                SetStatus("Не удалось завершить проверку/подпись КС-2: " + ex.Message, true);
            }
        }
        finally
        {
            _signingAct = false;
            SignedCheck.IsEnabled = true;
            if (SameAct() && IsLoaded)
            {
                // An in-flight cell save owns its own reload; never race it.
                if (_inSave) RevertSignedCheck();
                else Reload();
            }
        }
    }

    private void Act_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_building || ActCombo.SelectedItem is not ActItem item) return;
        SelectAct(item.Info.Id);
    }

    // -------------------------------------------------------------- СМР / транспорт / ПНР

    private async void SmrInput_Commit(object sender, RoutedEventArgs e) =>
        await SaveChargeAsync(ActChargeKind.Smr);

    private async void TrInput_Commit(object sender, RoutedEventArgs e) =>
        await SaveChargeAsync(ActChargeKind.Transport);

    private async void PnrInput_Commit(object sender, RoutedEventArgs e) =>
        await SaveChargeAsync(ActChargeKind.Pnr);

    private enum ActChargeKind { Smr, Transport, Pnr }

    /// <summary>Сумма в плитке (ks_view._save_amount): опечатка не затирает сохранённое,
    /// больше остатка по договору — предупреждение.</summary>
    private async Task SaveChargeAsync(ActChargeKind kind)
    {
        var current = Current;
        var totals = _totals;
        if (_building || _loadingAct || _loadFailed || string.IsNullOrEmpty(_revision) ||
            ActWritePending || current is null || current.Signed || _contractLocked || totals is null || _details is null) return;
        if(kind!=ActChargeKind.Pnr&&!string.IsNullOrEmpty(_details.SectionAmountsJson))return;
        var input = kind switch
        {
            ActChargeKind.Smr => SmrInput,
            ActChargeKind.Transport => TrInput,
            _ => PnrInput,
        };
        decimal v;
        try
        {
            v = string.IsNullOrWhiteSpace(input.Text) ? 0m
                : NumberText.Parse(input.Text)
                  ?? throw new InvalidOperationException("Некорректная сумма");
        }
        catch (Exception)
        {
            SetStatus($"Не число: «{input.Text}». Пишите цифрами, например 12 или 1 250,50", true);
            FillTiles();
            return;
        }
        var oldValue = kind switch
        {
            ActChargeKind.Smr => current.Smr,
            ActChargeKind.Transport => current.Transport,
            _ => current.Pnr,
        };
        if (Math.Abs(oldValue - v) < 0.005m) return;
        if (kind == ActChargeKind.Smr && _isPosSmr)
        {
            // СМР «по поз.» считаются по строкам акта, ручной ввод запрещён
            FillTiles();
            return;
        }
        var smr = kind == ActChargeKind.Smr ? v : _details.Smr;
        var tr = kind == ActChargeKind.Transport ? v : _details.Transport;
        var pnr = kind == ActChargeKind.Pnr ? v : _details.Pnr;
        try
        {
            var edit = new SqlServerActs.ActDraftEdit(_details.Number, _details.Date, _details.PeriodFrom,
                _details.PeriodTo, smr, tr, pnr, _details.SmrBySystem,
                _details.DsNo, _details.DsDate, _details.DsExtra);
            var saved = await RunActWriteAsync(() => SqlServerActs.SaveDraft(
                _connectionString, _actId, edit, ServerRevision()));
            if (!saved.Saved)
            {
                SetStatus("Ошибка: " + saved.Error, true);
                _revision = "";
                Reload();
                return;
            }
            _revision = saved.Revision;
            var contractValue = kind switch
            {
                ActChargeKind.Smr => totals.Smr,
                ActChargeKind.Transport => totals.Transport,
                _ => totals.Pnr,
            };
            var other = _acts.Where(a => a.Id != current.Id).Sum(a => kind switch
            {
                ActChargeKind.Smr => a.Smr,
                ActChargeKind.Transport => a.Transport,
                _ => a.Pnr,
            });
            var left = contractValue - other;
            if (v - left > 0.005m)
                SetStatus($"Внимание: это больше остатка по договору на {Money.Format(v - left)}", true);
            else
                SetStatus("Сумма сохранена");
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Исход сохранения неизвестен: " + ex.Message + " Обновите КС перед новой правкой.", true);
        }
        Reload();
    }

    private async void SmrBySystem_Changed(object sender, RoutedEventArgs e)
    {
        var current = Current;
        if (_building || ActWritePending || current is null || current.Signed || _contractLocked || _details is null) return;
        if(!string.IsNullOrEmpty(_details.SectionAmountsJson))return;
        var value = SmrBySystemCheck.IsChecked == true;
        if (value == _details.SmrBySystem) return;
        try
        {
            var edit = new SqlServerActs.ActDraftEdit(_details.Number, _details.Date, _details.PeriodFrom,
                _details.PeriodTo, _details.Smr, _details.Transport, _details.Pnr, value,
                _details.DsNo, _details.DsDate, _details.DsExtra);
            var saved = await RunActWriteAsync(() => SqlServerActs.SaveDraft(
                _connectionString, _actId, edit, ServerRevision()));
            if (!saved.Saved)
            {
                SetStatus("Ошибка: " + saved.Error, true);
                _revision = "";
                Reload();
                return;
            }
            _revision = saved.Revision;
            SetStatus("СМР по системам: " + (value ? "включено" : "выключено"));
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Исход сохранения неизвестен: " + ex.Message, true);
        }
        Reload();
    }

    private void TileInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            if (sender is TextBox box)
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
    }

    // -------------------------------------------------------------- позиции вне сметы


    private sealed record ComboPair(long Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>«Изменить позицию вне сметы…» (acts.update_extra_line): общие с другими
    /// актами строки клонируются, подписанные документы не меняются.</summary>
    private async void EditExtraDialog(long positionId, Func<bool>? menuCurrent = null)
    {
        var target = CaptureActCommand();
        var current = Current;
        if (target is null || current is null || current.Signed || _contractLocked ||
            _sheet is null || menuCurrent?.Invoke() == false) return;
        var line = _sheet.Positions.FirstOrDefault(p => p.PositionId == positionId);
        if (line is null || !line.OutOfKp ||
            _rows?.Any(row => !row.IsSection && row.PositionId == positionId && row.InAct) != true) return;
        var dialog = new Window
        {
            Title = "Изменить позицию вне сметы",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Window.GetWindow(this),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Наименование, единица и цена строки этого акта. Строка, общая с другими актами, " +
                   "получит свою копию — подписанные акты не изменятся.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
        });
        var nameBox = new TextBox { Text = line.Name, Margin = new Thickness(0, 4, 0, 0) };
        var unitBox = new TextBox { Text = line.Unit, MaxWidth = 120, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0) };
        var priceBox = new TextBox { Text = line.LinePrice.ToString("0.######", CultureInfo.CurrentCulture),
            Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Наименование", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = "Единица", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(unitBox);
        panel.Children.Add(new TextBlock { Text = "Цена за единицу, ₽", Margin = new Thickness(0, 10, 0, 0) });
        panel.Children.Add(priceBox);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new Button
        {
            Content = "Сохранить",
            Style = (Style)FindResource("PythonToolbarPrimary"),
            Padding = new Thickness(14, 5, 14, 5),
        };
        var cancel = new Button { Content = "Отмена", Style = (Style)FindResource("PythonToolbarButton"),
            Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        (string Name, string Unit, decimal Price)? edit = null;
        cancel.IsCancel = true;
        ok.Click += (_, _) =>
        {
            try
            {
                if (nameBox.Text.Trim().Length == 0)
                    throw new InvalidOperationException("Впишите наименование позиции");
                if (unitBox.Text.Trim().Length == 0 || unitBox.Text.Trim().Length > 32)
                    throw new InvalidOperationException("Единица измерения должна содержать от 1 до 32 символов");
                var price = NumberText.Parse(priceBox.Text)
                    ?? throw new InvalidOperationException("Введите неотрицательную цену");
                if (price < 0m || price > 1000000000000m)
                    throw new InvalidOperationException("Цена вне допустимого диапазона");
                RequireCurrentActCommand(target);
                if (_signed || _contractLocked || menuCurrent?.Invoke() == false)
                    throw new InvalidOperationException("Выбор или состояние акта изменились. Откройте команду снова.");
                edit = (nameBox.Text.Trim(), unitBox.Text.Trim(), price);
                dialog.DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(dialog, ex.Message, "Позиция вне сметы", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        if (dialog.ShowDialog() != true || edit is not { } values) return;
        if (!IsCurrentActCommand(target) || _signed || _contractLocked || menuCurrent?.Invoke() == false ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Cell, true) ||
            !ActGrid.CommitEdit(DataGridEditingUnit.Row, true) ||
            !IsCurrentActCommand(target) || _signed || _contractLocked || menuCurrent?.Invoke() == false)
        {
            SetStatus("Выбор или данные изменились. Откройте команду заново.", true);
            return;
        }
        bool SameAct() => _actId == target.ActId && _contractId == target.ContractId &&
            _connectionString == target.ConnectionString;
        _revision = "";
        SetStatus("Сохранение позиции вне сметы…");
        long savedId;
        try
        {
            savedId = await RunActWriteAsync(() => SqlServerActs.UpdateExtraLine(
                target.ConnectionString, target.ActId, positionId,
                values.Name, values.Unit, values.Price, target.Revision));
        }
        catch (Exception ex)
        {
            if (!SameAct()) return;
            SetStatus("Результат сохранения не подтверждён: " + ex.Message +
                ". Проверьте перечитанные данные перед повтором.", true);
            await ReloadAsync(target.ActId, reuseList: false);
            return;
        }
        if (!SameAct()) return;
        await ReloadAsync(target.ActId, reuseList: false);
        if (!SameAct() || _loadFailed || string.IsNullOrWhiteSpace(_revision)) return;
        if (_rows?.FirstOrDefault(row => !row.IsSection && row.PositionId == savedId && row.InAct) is { } restored)
        {
            ActGrid.SelectedItem = restored;
            ActGrid.ScrollIntoView(restored);
            SetStatus("Позиция вне сметы изменена");
        }
        else SetStatus("Запись подтверждена SQL, но строка не найдена в текущем представлении.", true);
    }

    // -------------------------------------------------------------- свойства акта

    /// <summary>«Свойства акта…»: номер, даты, период; ДС — у договора (ds_per_act).</summary>
    private void Props_Click(object sender, RoutedEventArgs e)
    {
        if (_actId == 0 || _signed)
        {
            if (_signed) SetStatus("Акт подписан — сначала снимите отметку «Подписан»", true);
            return;
        }
        var target = CaptureActCommand();
        var details = _details;
        if (target is null || details is null) return;
        var result = KsActPropertiesDialog.Show(this, details, _isContract && _dsPerAct);
        if (result is { } accepted)
            _ = SavePropsAsync(target, accepted.Edit, accepted.Message);
    }

    private async Task SavePropsAsync(ActCommandTarget target, SqlServerActs.ActDraftEdit edit, string message)
    {
        if (!IsCurrentActCommand(target))
        {
            SetStatus("Акт или данные изменились. Откройте свойства заново.", true);
            return;
        }
        SetStatus("Сохранение свойств акта…");
        try
        {
            var saved = await RunActWriteAsync(() => SqlServerActs.SaveDraft(
                target.ConnectionString, target.ActId, edit, target.Revision));
            if (!saved.Saved)
            {
                SetStatus("Не сохранено: " + saved.Error, true);
                _revision = "";
                Reload();
                return;
            }
            _revision = saved.Revision;
            SetStatus(message.Length > 0 ? message : "Свойства акта сохранены");
            Reload();
        }
        catch (Exception ex)
        {
            _revision = "";
            SetStatus("Исход сохранения неизвестен: " + ex.Message + " Обновите КС перед новой правкой.", true);
        }
    }

    // -------------------------------------------------------------- меню «Ещё»

    private void InitializeActMenuIcons()
    {
        ContextMenuIcons.Set(PreSignMenuItem, "Alert");
        ContextMenuIcons.Set(VorMenuItem, "Download");
        ContextMenuIcons.Set(ExportAllMenuItem, "Download");
        ContextMenuIcons.Set(ExportGroupMenuItem, "Download");
        ContextMenuIcons.Set(ExtraMenuItem, "Plus");
        ContextMenuIcons.Set(DeleteActMenuItem, "Trash", danger: true);
        ContextMenuIcons.Set(ActDiscountMenuItem, "Edit");
        ContextMenuIcons.Set(SectionAmountsMenuItem, "Layers");
        ContextMenuIcons.Set(IssuedMenuItem, "Upload");
        ContextMenuIcons.Set(RestAllMenuItem, "Check");
    }

    // Re-evaluate on every popup opening, including keyboard and right-click.
    private void ActMoreMenu_Opened(object sender, RoutedEventArgs e)
    {
        var current = Current;
        var ready = CaptureActCommand() is not null && current is not null && _sheet is not null;
        if (sender is ContextMenu menu)
        {
            var target = ready ? CaptureActCommand() : null;
            var snapshot = target is not null && _sheet is not null
                ? new ActMoreSnapshot(target, _sheet, ActGrid.SelectedItems.OfType<KsRowVm>().ToArray()) : null;
            foreach (var item in menu.Items.OfType<MenuItem>()) item.Tag = snapshot;
        }
        var unsigned = ready && current is { Signed: false };
        var editable = unsigned && !_contractLocked;
        var manualSections = !string.IsNullOrEmpty(_details?.SectionAmountsJson);
        PreSignMenuItem.IsEnabled = ready;
        ExportGroupMenuItem.IsEnabled = ready && !_exportInProgress;
        VorMenuItem.IsEnabled = ExportAllMenuItem.IsEnabled = ready && !_exportInProgress;
        DeleteActMenuItem.IsEnabled = editable;
        ExtraMenuItem.IsEnabled = IssuedMenuItem.IsEnabled = editable;
        RestAllMenuItem.IsEnabled = editable && !manualSections && _totals is not null;
        var adjustmentAvailable = ActDiscountMenuItem.Visibility == Visibility.Visible &&
            _details?.SupportsAdjustment == true;
        ActDiscountMenuItem.IsEnabled = SectionAmountsMenuItem.IsEnabled =
            editable && adjustmentAvailable && _totals is not null;
    }

    private void HeaderMore_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private void BarMore_Click(object sender, RoutedEventArgs e) => OpenMenu(sender);

    private static void OpenMenu(object sender)
    {
        if (sender is Button button && button.ContextMenu is not null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }
    }
}
