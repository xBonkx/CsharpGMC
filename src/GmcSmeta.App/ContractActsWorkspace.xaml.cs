using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Строка списка актов слева (ks_view act_list): «КС-2 № … от …» и сумма,
/// подписанные — зелёным.</summary>
public sealed record KsActListVm(long Id, string Title, string Caption, bool Signed)
{
    public System.Windows.Media.Brush Fore => Signed
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x65, 0x34))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A));
}

/// <summary>Строка сводной (acts.summary_rows): раздел или позиция с объёмами по актам.</summary>
public sealed class KsSummaryVm
{
    public bool IsTop { get; init; }
    public bool IsSub { get; init; }
    public string Num { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Done { get; init; } = "";
    public string Left { get; init; } = "";
    public string LeftSum { get; init; } = "";
    public string Issued { get; init; } = "";
    public bool FullyClosed { get; init; }
    public long PositionId { get; init; }
    public decimal LeftRaw { get; init; }
    public IReadOnlyDictionary<long, decimal> PerAct { get; init; } =
        new Dictionary<long, decimal>();
}

/// <summary>Строка «Вне подписанной сметы» (acts.extra_rows) или её итоги.</summary>
public sealed class KsExtraVm
{
    public string System { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string SignedQty { get; init; } = "";
    public string DraftQty { get; init; } = "";
    public string Price { get; init; } = "";
    public string Amount { get; init; } = "";
    public string ActStatus { get; init; } = "";
    public bool Bold { get; init; }
    public bool Muted { get; init; }
    public bool IsChild { get; init; }
}

public sealed class KsActValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not IReadOnlyDictionary<long, decimal> source ||
            parameter is not long actId || !source.TryGetValue(actId, out var quantity) ||
            quantity == 0m) return "";
        return quantity.ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Вкладка договора «КС-2 / КС-3» (ks_view.py): слева список актов документа,
/// справа редактор акта; второй режим — «Сводная по выполнению» с разделом
/// «Вне подписанной сметы» и плитками прогресса договора. Запись идёт только
/// через редактор (ActsEditorView), дублирующего пути записи нет.
/// </summary>
public partial class ContractActsWorkspace : UserControl
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private const string KsHintText =
        "Нажмите «Новый акт», выделите строки или систему и нажмите «Закрыть выделенное» " +
        "(или правой кнопкой мыши) — или впишите количество двойным щелчком в «В этом акте». " +
        "Внизу впишите СМР за этот акт. Потом «Сохранить КС-2 и КС-3».";
    private readonly KsActValueConverter _qtyConverter = new();
    private readonly EstimateUiSettings _uiSettings = EstimateUiSettings.Load();
    private string _connectionString = "";
    private string _title = "";
    private int _contractId;
    private int _summaryVersion;
    private string _summaryRevision = "";
    private CancellationTokenSource? _summaryCancellation;
    private bool _bound;
    private bool _syncingList;
    internal bool HasCacheActivity => EmbeddedEditor.HasCacheActivity ||
        _summaryCancellation is not null || _summaryExportCancellation is not null;
    private IReadOnlyList<SqlServerActs.ExtraRow> _extras = Array.Empty<SqlServerActs.ExtraRow>();
    private ContractTotals? _totals;
    private List<SqlServerActs.ActInfo> _acts = new();
    private List<SqlServerActs.ActInfo> _summaryActs = new();
    private SqlServerActs.SummaryData? _summary;
    private List<KsSummaryVm>? _summaryAllRows;
    private List<KsSummaryVm>? _summaryRestRows;
    private readonly Dictionary<long, KsSummaryVm> _summaryPositionViews = new();
    private (long Id, string Number)[] _summaryColumnKeys = Array.Empty<(long, string)>();

    public ContractActsWorkspace()
    {
        InitializeComponent();
        HintText.Text = KsHintText;
        HintBar.Visibility = _uiSettings.KsHintHidden ? Visibility.Collapsed : Visibility.Visible;
        EmbeddedEditor.ActsChanged += Editor_ActsChanged;
        EmbeddedEditor.ListToggleChanged += Editor_ListToggleChanged;
        EmbeddedEditor.CompactChanged += Editor_CompactChanged;
        Unloaded += (_, _) => InvalidateSummary();
        Loaded += (_, _) => ResumeSummaryIfNeeded();
        IsVisibleChanged += (_, _) =>
        {
            // A cached contract tab can be collapsed without being unloaded.
            if (!IsVisible)
            {
                CancelSummaryRead();
            }
            else ResumeSummaryIfNeeded();
        };
    }

    public void Bind(string connectionString, int contractId, string title)
    {
        if (_bound && _contractId == contractId && _connectionString == connectionString)
        {
            EmbeddedEditor.RefreshOnTabReturn();
            // The progress view may still be selected when returning from
            // another contract tab. Revalidate it independently of the editor.
            if (SummaryPage.Visibility == Visibility.Visible && IsLoaded)
                _ = RefreshSummaryAsync();
            return;
        }
        InvalidateSummary();
        _bound = true;
        _connectionString = connectionString;
        _contractId = contractId;
        _title = title;
        EmbeddedEditor.Bind(_connectionString, contractId, title);
        EmbeddedEditor.ShowActSelector(false);
        EmbeddedEditor.ShowListToggle(true);
        ListToggleApply(_uiSettings.KsListOpen, save: false);
        ShowMode("acts");
    }

    private void Editor_ActsChanged(object? sender, EventArgs e) => RefreshActList();

    private void Editor_ListToggleChanged(object? sender, bool open) => ListToggleApply(open);

    private void ListToggleApply(bool open, bool save = true)
    {
        LeftColumn.Width = open ? new GridLength(250) : new GridLength(0);
        LeftBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (save)
        {
            _uiSettings.KsListOpen = open;
            _uiSettings.Save();
        }
    }

    private void Editor_CompactChanged(object? sender, bool compact)
    {
        ModeBarPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HintBar.Visibility = compact || _uiSettings.KsHintHidden
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void HintClose_Click(object sender, RoutedEventArgs e)
    {
        HintBar.Visibility = Visibility.Collapsed;
        _uiSettings.KsHintHidden = true;
        _uiSettings.Save();
    }

    /// <summary>Меню «Показать подсказки снова»: вернуть подсказку КС, если вкладка не компактная.</summary>
    internal void ShowHintsAgain()
    {
        _uiSettings.KsHintHidden = false;
        _uiSettings.Save();
        // Компактный режим прячет подсказку независимо от настройки (Editor_CompactChanged).
        if (EmbeddedEditor.CompactToggle.IsChecked != true)
            HintBar.Visibility = Visibility.Visible;
    }


    // ------------------------------------------------------------------ список актов

    private void RefreshActList()
    {
        if (!_bound) return;
        // The editor and list use exactly the same snapshot and selected ID.
        // No second synchronous SQL read while handling a selection event.
        _acts = EmbeddedEditor.ActsSnapshot.ToList();
        var selectedId = EmbeddedEditor.SelectedActId;
        var items = _acts.Select(a => new KsActListVm(a.Id,
                $"КС-2 № {a.Number}" + (a.Date == default ? "" : $" от {a.Date:dd.MM.yyyy}"),
                $"{Money.Format(a.Total)} ₽" + (a.Signed ? "   ✓ подписан" : ""),
                a.Signed)).ToList();
        _syncingList = true;
        try
        {
            if (ActList.ItemsSource is not List<KsActListVm> existing || !existing.SequenceEqual(items))
                ActList.ItemsSource = items;
            ActList.SelectedItem = (ActList.ItemsSource as List<KsActListVm>)
                ?.FirstOrDefault(item => item.Id == selectedId);
        }
        finally { _syncingList = false; }
    }

    private void ActList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        if (ActList.SelectedItem is KsActListVm item && item.Id != EmbeddedEditor.SelectedActId)
            EmbeddedEditor.SelectAct(item.Id);
    }

    private void NewAct_Click(object sender, RoutedEventArgs e) => EmbeddedEditor.CreateAct();

    private void Requisites_Click(object sender, RoutedEventArgs e)
    {
        if (!_bound) return;
        var dlg = new KsRequisitesWindow(_connectionString, _contractId);
        var owner = Window.GetWindow(this);
        if (owner is not null) dlg.Owner = owner;
        dlg.ShowDialog();
    }

    private void ContractActs_Click(object sender, RoutedEventArgs e)
    {
        if (!_bound) return;
        try
        {
            var window = new AllContractActsWindow(_connectionString, _contractId, _title);
            var owner = Window.GetWindow(this);
            if (owner is not null) window.Owner = owner;
            window.ShowDialog();
            EmbeddedEditor.RefreshFromServer();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("AllContractActsWindow: {0}", ex);
            MessageBox.Show("Не удалось открыть все КС договора:\n" + ex.Message,
                "КС-2 / КС-3", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ режимы

    private void ActsMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ActsPage is null) return;
        ShowMode("acts");
    }

    private void ProgressMode_Checked(object sender, RoutedEventArgs e)
    {
        if (ActsPage is null) return;
        ShowMode("progress");
    }

    private void ShowMode(string mode)
    {
        var progress = mode == "progress";
        ActsPage.Visibility = progress ? Visibility.Collapsed : Visibility.Visible;
        SummaryPage.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        SummaryBarPanel.Visibility = progress ? Visibility.Visible : Visibility.Collapsed;
        if (progress && _bound && IsLoaded) _ = RefreshSummaryAsync();
        else if (!progress) CancelSummaryRead();
    }

    // ------------------------------------------------------------------ сводная

    private void ResumeSummaryIfNeeded()
    {
        if (_bound && IsLoaded && IsVisible && SummaryPage.Visibility == Visibility.Visible &&
            _summaryCancellation is null)
            _ = RefreshSummaryAsync();
    }

    private void CancelSummaryRead()
    {
        _summaryExportCancellation?.Cancel();
        ++_summaryVersion;
        var cancellation = _summaryCancellation;
        _summaryCancellation = null;
        cancellation?.Cancel();
    }

    private void InvalidateSummary()
    {
        CancelSummaryRead();
        ClearSummarySnapshot();
    }

    private void ClearSummarySnapshot()
    {
        _summaryRevision = "";
        _summary = null;
        _totals = null;
        _extras = Array.Empty<SqlServerActs.ExtraRow>();
        _summaryActs.Clear();
        _summaryAllRows = null;
        _summaryRestRows = null;
        _summaryPositionViews.Clear();
        if (SummaryGrid is not null) SummaryGrid.ItemsSource = null;
        if (ExtraGrid is not null) ExtraGrid.ItemsSource = null;
        if (ExtraBox is not null) ExtraBox.Visibility = Visibility.Collapsed;
    }

    private async Task RefreshSummaryAsync()
    {
        if (!_bound || !IsLoaded || !IsVisible || SummaryPage.Visibility != Visibility.Visible ||
            _summaryCancellation is not null) return;
        var knownRevision = _summary is null ? "" : _summaryRevision;
        _summaryExportCancellation?.Cancel();
        var version = ++_summaryVersion;
        using var cancellation = new CancellationTokenSource();
        _summaryCancellation = cancellation;
        var token = cancellation.Token;
        var connectionString = _connectionString;
        var contractId = _contractId;
        var cached = EmbeddedEditor.CaptureReadContext(connectionString, contractId);
        try
        {
            var (summary, extras, totals, revision) = await Task.Run(async () =>
            {
                using var conn = await SqlServerDatabase.OpenAsync(connectionString, token).ConfigureAwait(false);
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    var before = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(before) && before == knownRevision)
                        return (Sheet: (SqlServerActs.SummaryData?)null, Extra: (IReadOnlyList<SqlServerActs.ExtraRow>?)null,
                            Totals: (ContractTotals?)null, Revision: before);
                    var common = cached is not null && cached.Revision == before &&
                        !string.IsNullOrWhiteSpace(before) ? cached : null;
                    ContractTotals? totals;
                    TagRules? rules;
                    if (common is { Totals: not null, Rules: not null })
                        (totals, rules) = (common.Totals, common.Rules);
                    else (totals, rules) = Totals.ReadOneWithRules(conn, contractId, token);
                    var (sheet, extra) = SqlServerActs.SummaryWithExtras(conn, contractId, token,
                        common?.Acts, rules, totals?.Form);
                    var after = await SqlServerDatabase.RevisionAsync(conn, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(before) && before == after)
                        return (Sheet: (SqlServerActs.SummaryData?)sheet, Extra: (IReadOnlyList<SqlServerActs.ExtraRow>?)extra, Totals: totals, Revision: before);
                }
                throw new InvalidOperationException("Данные КС изменились во время чтения. Повторите загрузку.");
            }, token);
            if (token.IsCancellationRequested || version != _summaryVersion || !IsLoaded || !IsVisible ||
                contractId != _contractId ||
                connectionString != _connectionString || SummaryPage.Visibility != Visibility.Visible) return;
            if (summary is null)
            {
                StartupTrace.Mark("ks_progress_revision_hit");
                return;
            }
            ClearSummarySnapshot();
            _summary = summary;
            _extras = extras!;
            _totals = totals;
            _summaryRevision = revision;
            _summaryActs = summary.Acts.ToList();
            BuildSummaryColumns();
            FillSummaryTiles();
            ApplySummaryFilter();
            BuildExtra();
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Navigation cancelled an independent read, not an act write.
        }
        catch (Exception ex)
        {
            if (version != _summaryVersion || !IsLoaded || !IsVisible) return;
            InvalidateSummary();
            MessageBox.Show(Window.GetWindow(this), "Ошибка сводной: " + ex.Message,
                "Сводная по выполнению", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(_summaryCancellation, cancellation)) _summaryCancellation = null;
        }
    }

    private void BuildSummaryColumns()
    {
        var ordered = _summaryActs.OrderBy(a => a.Date).ThenBy(a => a.Id).ToArray();
        var keys = ordered.Select(a => (a.Id, a.Number)).ToArray();
        if (SummaryGrid.Columns.Count == keys.Length + 8 && _summaryColumnKeys.SequenceEqual(keys))
            return;

        while (SummaryGrid.Columns.Count > 4)
            SummaryGrid.Columns.RemoveAt(4);
        foreach (var act in ordered)
        {
            // заголовок столбца — только номер акта (дата — в заголовке листа сводной)
            SummaryGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "№ " + act.Number,
                Binding = new Binding(nameof(KsSummaryVm.PerAct))
                {
                    Converter = _qtyConverter,
                    ConverterParameter = act.Id,
                },
                Width = new DataGridLength(110),
                IsReadOnly = true,
                ElementStyle = (Style)FindResource("CompactCostCenter"),
                HeaderStyle = (Style)FindResource("KsQuantityHeader"),
            });
        }
        AddTextColumn("Сдано всего", nameof(KsSummaryVm.Done), 96);
        AddTextColumn("Остаток", nameof(KsSummaryVm.Left), 84);
        AddTextColumn("Остаток, ₽", nameof(KsSummaryVm.LeftSum), 110, right: true);
        AddTextColumn("Выдано на объект", nameof(KsSummaryVm.Issued), 110);
        _summaryColumnKeys = keys;
    }

    private void AddTextColumn(string header, string property, double width, bool right = false)
    {
        SummaryGrid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(property),
            Width = new DataGridLength(width),
            IsReadOnly = true,
            ElementStyle = (Style)FindResource(right ? "CompactCostMoney" : "CompactCostCenter"),
            HeaderStyle = (Style)FindResource(right ? "KsMoneyHeader" : "KsQuantityHeader"),
        });
    }

    private static string QtyText(decimal v) => v == decimal.Truncate(v)
        ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.###", CultureInfo.CurrentCulture);

    private List<KsSummaryVm> BuildSummaryRows(bool onlyRest)
    {
        var rows = new List<KsSummaryVm>();
        var summary = _summary;
        if (summary is null) return rows;
        // One indexed projection for the current filter, preserving section order.
        foreach (var (system, sectionHeader, positions) in KsWorkspaceProjection.VisibleSummaryGroups(summary, onlyRest))
        {
            if (sectionHeader)
            {
                rows.Add(new KsSummaryVm { IsTop = true, Name = system.Name });
                continue;
            }
            rows.Add(new KsSummaryVm
            {
                IsTop = !system.IsSub,
                IsSub = system.IsSub,
                Name = (system.IsSub ? "      ↳  " : "") + system.Name,
            });
            foreach (var p in positions)
            {
                if (!_summaryPositionViews.TryGetValue(p.PositionId, out var item))
                {
                    item = new KsSummaryVm
                    {
                        PositionId = p.PositionId,
                        Num = p.NumText,
                        Name = p.Name,
                        Unit = p.Unit,
                        Qty = QtyText(p.QtyByKp),
                        Done = p.Done == 0m ? "" : QtyText(p.Done),
                        Left = Math.Abs(p.Left) > 0.000000001m ? QtyText(p.Left) : "—",
                        LeftSum = Math.Abs(p.LeftSum) > 0.005m ? Money.Format(p.LeftSum) : "",
                        Issued = p.Issued == 0m ? "" : QtyText(p.Issued),
                        FullyClosed = Math.Abs(p.Left) <= 0.000001m,
                        LeftRaw = p.Left,
                        PerAct = p.PerAct,
                    };
                    _summaryPositionViews.Add(p.PositionId, item);
                }
                rows.Add(item);
            }
        }
        return rows;
    }

    private void ApplySummaryFilter()
    {
        if (SummaryGrid is null || _summary is null) return;
        var rows = SummaryRestOnly.IsChecked == true
            ? (_summaryRestRows ??= BuildSummaryRows(true))
            : (_summaryAllRows ??= BuildSummaryRows(false));
        if (!ReferenceEquals(SummaryGrid.ItemsSource, rows))
            SummaryGrid.ItemsSource = rows;
    }

    private void SummaryRest_Changed(object sender, RoutedEventArgs e) => ApplySummaryFilter();

    private void FillSummaryTiles()
    {
        var totals = _totals;
        var acts = _summaryActs;
        if (totals is null) return;
        // прогресс по договору (acts.contract_progress): «Сдано» = только подписанные
        var matClosed = acts.Sum(a => a.Materials);
        var smrClosed = acts.Sum(a => a.Smr);
        var signed = acts.Where(a => a.Signed).Sum(a => a.Total);
        var pending = acts.Where(a => !a.Signed).Sum(a => a.Total);
        var total = totals.Total;
        decimal Pct(decimal part, decimal whole) =>
            whole == 0m ? 0m : decimal.Round(part / whole * 100m, 0, MidpointRounding.ToEven);
        SumMatValue.Text = $"{Money.Format(matClosed)}  ·  {Pct(matClosed, totals.Materials):0}%";
        SumSmrValue.Text = $"{Money.Format(smrClosed)}  ·  {Pct(smrClosed, totals.Smr):0}%";
        SumTotalValue.Text = $"{Money.Format(signed)}  ·  {Pct(signed, total):0}%";
        SumTotalCaption.Text = "Всего сдано";
        SumPendingValue.Text = Money.Format(pending);
        var drafts = acts.Where(a => !a.Signed)
            .Select(a => $"КС-2 №{a.Number}: {Money.Format(a.Total)} ₽").ToList();
        SumPendingValue.ToolTip = drafts.Count > 0 ? string.Join("\n", drafts) : "Нет неподписанных актов";

        // суммы вне сметы показываются отдельно, плитки договора — только по смете
        var extraSigned = decimal.Round(_extras.Sum(r => r.SignedSum), 2);
        var extraDraft = decimal.Round(_extras.Sum(r => r.DraftSum), 2);
        var contractSigned = decimal.Round(signed - extraSigned, 2);
        var contractLeft = decimal.Round(total - contractSigned - pending, 2);
        SumTotalCaption.Text = "Сдано по смете";
        SumTotalValue.Text = $"{Money.Format(contractSigned)}  ·  " +
            $"{(total == 0m ? 0m : decimal.Round(contractSigned / total * 100m, 0, MidpointRounding.ToEven)):0}%";
        SumLeftValue.Text = Money.Format(contractLeft);
        SumExtraCaption.Text = "Вне сметы (подписано)";
        SumExtraValue.Text = Money.Format(extraSigned);
        SumExtraValue.ToolTip = $"Черновики: {Money.Format(extraDraft)}";
        SumAllValue.Text = Money.Format(signed);
        SumAllValue.ToolTip = $"По смете {Money.Format(contractSigned)} + вне сметы {Money.Format(extraSigned)}; " +
            $"черновики {Money.Format(pending)}";
        ExtrasTilesPanel.Visibility = _extras.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildExtra()
    {
        var rows = new List<KsExtraVm>();
        var extras = _extras;
        foreach (var r in extras)
        {
            var price = r.Price is decimal p ? Money.Format(p)
                : (r.Lines.Count > 0 ? "разные" : "");
            rows.Add(new KsExtraVm
            {
                System = r.System,
                Name = r.Name,
                Unit = r.Unit,
                SignedQty = r.SignedQty == 0m ? "" : QtyText(r.SignedQty),
                DraftQty = r.DraftQty == 0m ? "" : QtyText(r.DraftQty),
                Price = price,
                Amount = Money.Format(r.Amount),
                ActStatus = string.Join(", ", r.Acts),
            });
            if (r.Lines.Count > 1)
            {
                foreach (var line in r.Lines)
                    rows.Add(new KsExtraVm
                    {
                        Unit = r.Unit,
                        SignedQty = line.Signed && line.Qty != 0m ? QtyText(line.Qty) : "",
                        DraftQty = !line.Signed && line.Qty != 0m ? QtyText(line.Qty) : "",
                        Price = Money.Format(line.Price),
                        Amount = Money.Format(line.Amount),
                        ActStatus = line.Act,
                        Muted = !line.Signed,
                        IsChild = true,
                    });
            }
        }
        var totals = _totals;
        if (rows.Count > 0 && totals is not null)
        {
            var signedSum = decimal.Round(extras.Sum(r => r.SignedSum), 2);
            var draftSum = decimal.Round(extras.Sum(r => r.DraftSum), 2);
            var signed = _summaryActs.Where(a => a.Signed).Sum(a => a.Total);
            var pending = _summaryActs.Where(a => !a.Signed).Sum(a => a.Total);
            rows.Add(new KsExtraVm
            {
                Name = "Итого вне сметы",
                Amount = Money.Format(decimal.Round(signedSum + draftSum, 2)),
                ActStatus = $"подписано {Money.Format(signedSum)} · черновики {Money.Format(draftSum)}",
                Bold = true,
            });
            rows.Add(new KsExtraVm
            {
                Name = "Всего с учётом вне сметы",
                Amount = Money.Format(signed),
                ActStatus = $"черновики {Money.Format(pending)}",
                Bold = true,
            });
        }
        ExtraBox.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExtraGrid.ItemsSource = rows;
    }


}
