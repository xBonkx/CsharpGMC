using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public sealed record Ks2RowVm(SqlServerActs.ActInfo Info)
{
    public long Id => Info.Id;
    public string Title => "КС-2 № " + Info.Number;
    public string Source => Info.SourceName;
    public string Caption => $"{Info.SourceName} · {Info.Date:dd.MM.yyyy} · {Money.Format(Info.Total)} ₽";
    public string Date => Info.Date.ToString("dd.MM.yyyy");
    public string Period => $"{Info.PeriodFrom:dd.MM.yyyy} — {Info.PeriodTo:dd.MM.yyyy}";
    public int Count => Info.Lines;
    public string Amount => Money.Format(Info.Total);
    public string Status => Info.Signed ? "Подписан" : "Черновик";
}

public sealed record Ks3GroupVm(KsWorkspaceProjection.Ks3Group Info, IReadOnlyList<Ks2RowVm> Acts)
{
    public string Title => Info.Title;
    public string Amount => Money.Format(Info.Total) + " ₽";
}

/// <summary>
/// «Все КС договора» (ks_view.b_contract_acts → contract_acts_requested): общий
/// список актов всех приложений и ДС одного договора, группы КС-3 (ks3_key),
/// суммы и статусы. Открытие акта — окно КС-2 этого документа.
/// </summary>
public partial class AllContractActsWindow : Window
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
    private readonly string _connectionString;
    private readonly int _contractId;
    private readonly string _title;
    private long _selectedActId;
    private string _selectedGroupKey = "";
    private SqlServerKsContractScope.Scope? _scope;
    private IReadOnlyList<SqlServerActs.ActInfo> _acts = Array.Empty<SqlServerActs.ActInfo>();
    private IReadOnlyList<KsWorkspaceProjection.Ks3Group> _groups =
        Array.Empty<KsWorkspaceProjection.Ks3Group>();
    private KsOverviewSearchIndex _searchIndex = new(Array.Empty<KsWorkspaceProjection.Ks3Group>());
    private Dictionary<long, Ks2RowVm> _rowViews = new();
    private Dictionary<long, Ks2RowVm> _visibleRows = new();
    private IReadOnlyList<KsWorkspaceProjection.Ks3Group>? _visibleGroups;
    private int _requestVersion;
    private bool _initialized;
    private bool _updatingSelection;
    private bool _closed;
    private bool _loading;
    private readonly CancellationTokenSource _readLifetime = new();

    public AllContractActsWindow(string connectionString, int contractId, string title)
    {
        _connectionString = connectionString;
        _contractId = contractId;
        _title = title;
        InitializeComponent();
        _initialized = true;
        Loaded += (_, _) => _ = RefreshActsAsync();
        Closed += (_, _) => { _closed = true; _requestVersion++; _readLifetime.Cancel(); _readLifetime.Dispose(); };
    }

    private async Task RefreshActsAsync()
    {
        if (!_initialized || _closed || _loading) return;
        var version = ++_requestVersion;
        var token = _readLifetime.Token;
        _loading = true;
        UpdateSelectedButtons();
        OverviewStatus.Text = "Загружаю акты из SQL Server…";
        try
        {
            var (scope, groups, search, views, signed, draft) = await Task.Run(() =>
            {
                var snapshot = SqlServerKsContractScope.ReadOverview(_connectionString, _contractId, token);
                var grouped = KsWorkspaceProjection.Build(snapshot.Acts);
                var index = new KsOverviewSearchIndex(grouped);
                var rows = snapshot.Acts.ToDictionary(a => a.Id, a => new Ks2RowVm(a));
                decimal signedAmount = 0m, draftAmount = 0m;
                foreach (var act in snapshot.Acts)
                {
                    token.ThrowIfCancellationRequested();
                    if (act.Signed) signedAmount += act.Total;
                    else draftAmount += act.Total;
                }
                return (snapshot, grouped, index, rows, signedAmount, draftAmount);
            }, token);
            if (token.IsCancellationRequested || _closed || version != _requestVersion) return;
            _scope = scope;
            var acts = scope.Acts;
            _acts = acts;
            _groups = groups;
            _searchIndex = search;
            _rowViews = views;
            _visibleGroups = null;
            ScopeTitle.Text = "Договор № " + scope.AgreementNumber +
                " · документов: " + scope.Documents.Count;
            TotalActsText.Text = acts.Count.ToString(Ru);
            SignedAmountText.Text = Money.Format(signed);
            DraftAmountText.Text = Money.Format(draft);
            ActsAmountText.Text = Money.Format(signed + draft);
            ApplyActsFilter();
            OverviewStatus.Text = acts.Count == 0
                ? "Актов по договору и ДС пока нет."
                : $"{acts.Count} КС-2 · {_groups.Count} КС-3 по договору и ДС";
        }
        catch (Exception ex)
        {
            if (!_closed && version == _requestVersion)
            {
                // A failed refresh can indicate a changed legal mapping. Never
                // keep showing the previous scope as if it were still valid.
                _scope = null;
                _acts = Array.Empty<SqlServerActs.ActInfo>();
                _groups = Array.Empty<KsWorkspaceProjection.Ks3Group>();
                _searchIndex = new KsOverviewSearchIndex(_groups);
                _rowViews.Clear();
                _visibleGroups = null;
                _selectedActId = 0;
                _selectedGroupKey = "";
                ScopeTitle.Text = "";
                TotalActsText.Text = SignedAmountText.Text = DraftAmountText.Text = ActsAmountText.Text = "—";
                ApplyActsFilter();
                OverviewStatus.Text = "Ошибка чтения актов: " + ex.Message;
            }
        }
        finally
        {
            if (!_closed && version == _requestVersion)
            {
                _loading = false;
                UpdateSelectedButtons();
            }
        }
    }

    private void ApplyActsFilter()
    {
        // SelectedIndex=0 in XAML fires SelectionChanged during InitializeComponent,
        // before ActsTree and ActListGrid have been created.
        if (!_initialized || _closed || _updatingSelection) return;
        bool? signed = ActsStatusFilter.SelectedIndex switch { 1 => false, 2 => true, _ => null };
        var groups = _searchIndex.Filter(ActsSearch.Text, signed);
        // A different search string may still yield precisely the same rows.
        // Keep the existing tree, scroll and selection in that case.
        if (_visibleGroups is not null && SameVisibleGroups(_visibleGroups, groups)) return;
        var viewModels = groups.Select(g => new Ks3GroupVm(g,
            g.Acts.Select(a => _rowViews[a.Id]).ToList())).ToList();
        var rows = viewModels.SelectMany(g => g.Acts).ToList();
        _visibleRows = rows.ToDictionary(r => r.Id);
        _visibleGroups = groups;
        var selected = _visibleRows.GetValueOrDefault(_selectedActId);
        _updatingSelection = true;
        try
        {
            ActsTree.ItemsSource = viewModels;
            ActListGrid.ItemsSource = rows;
            ActListGrid.SelectedItem = selected;
            SetSelectedAct(selected?.Info);
        }
        finally { _updatingSelection = false; }
        UpdateSelectedButtons();
    }

    private static bool SameVisibleGroups(IReadOnlyList<KsWorkspaceProjection.Ks3Group> a,
        IReadOnlyList<KsWorkspaceProjection.Ks3Group> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!ReferenceEquals(a[i], b[i]) &&
                (a[i].Key != b[i].Key || !a[i].Acts.SequenceEqual(b[i].Acts))) return false;
        return true;
    }

    private void ActsSearch_Changed(object sender, TextChangedEventArgs e) => ApplyActsFilter();
    private void ActsStatusFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyActsFilter();

    private void SetSelectedAct(SqlServerActs.ActInfo? act)
    {
        _selectedActId = act?.Id ?? 0;
        _selectedGroupKey = act is null ? "" : KsWorkspaceProjection.GroupKey(act);
    }

    private void ActsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_initialized || _updatingSelection || _closed) return;
        var row = e.NewValue switch
        {
            Ks2RowVm act => act,
            Ks3GroupVm group => group.Acts.FirstOrDefault(),
            _ => null,
        };
        _updatingSelection = true;
        try
        {
            var visible = row is null ? null : _visibleRows.GetValueOrDefault(row.Id);
            ActListGrid.SelectedItem = visible;
            SetSelectedAct(visible?.Info);
        }
        finally { _updatingSelection = false; }
        UpdateSelectedButtons();
    }

    private void ActListGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingSelection || _closed) return;
        SetSelectedAct((ActListGrid.SelectedItem as Ks2RowVm)?.Info);
        UpdateSelectedButtons();
    }

    private void UpdateSelectedButtons()
    {
        if (!_initialized || _closed) return;
        OpenSelectedButton.IsEnabled = !_loading && _scope is not null && _selectedActId != 0 &&
            _visibleRows.ContainsKey(_selectedActId);
        OpenKs3Button.IsEnabled = OpenSelectedButton.IsEnabled && _scope is not null &&
            !string.IsNullOrWhiteSpace(_selectedGroupKey) &&
            _groups.Any(g => g.Key == _selectedGroupKey);
    }

    private void ActsTree_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void ActListGrid_DoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenSelected_Click(object sender, RoutedEventArgs e) => OpenSelected();

    private void OpenSelected()
    {
        if (_loading || _scope is null) return;
        var act = _visibleRows.GetValueOrDefault(_selectedActId)?.Info;
        if (act is null) return;
        try
        {
            var window = new ActsWindow(_connectionString, checked((int)act.ContractId), act.SourceName, act.Id)
            { Owner = this };
            window.ShowDialog();
            _ = RefreshActsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Open KS-2 {0}: {1}", act.Id, ex);
            OverviewStatus.Text = "Не удалось открыть КС-2: " + ex.Message;
        }
    }

    private void OpenKs3_Click(object sender, RoutedEventArgs e) => OpenGroupKs3();

    private void OpenGroupKs3()
    {
        if (_loading || _scope is null || string.IsNullOrWhiteSpace(_selectedGroupKey) ||
            !_groups.Any(g => g.Key == _selectedGroupKey)) return;
        try
        {
            var report = new KsGroupReportWindow(_connectionString, _contractId, _selectedGroupKey)
            { Owner = this };
            report.ShowDialog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Open KS-3: {0}", ex);
            OverviewStatus.Text = "Не удалось открыть КС-3: " + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshActsAsync();
}
