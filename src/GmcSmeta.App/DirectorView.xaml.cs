using System.Globalization;
using System.Threading;
using ClosedXML.Excel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Строка сводки руководителя — отображение (director_view.refresh).</summary>
public sealed class DirectorPreviewRow
{
    public long ObjectId { get; init; }
    public string Object { get; init; } = "";
    public string Total { get; init; } = "";
    public string Closed { get; init; } = "";
    public string Margin { get; init; } = "";
    public string Coverage { get; init; } = "";
    public string SmrMargin { get; init; } = "";
    public string OutCost { get; init; } = "";
    public string Earned { get; init; } = "";
    public string Progress { get; init; } = "";
    public string Late { get; init; } = "";
    // Raw values are for numeric sorting and progress; export strings stay unchanged.
    public string DisplayObject { get; init; } = "";
    public string WarningTip { get; init; } = "";
    public bool Incomplete { get; init; }
    public bool IncompleteCoverage { get; init; }
    public decimal TotalValue { get; init; }
    public decimal ClosedValue { get; init; }
    public decimal? MarginValue { get; init; }
    public decimal? CoverageValue { get; init; }
    public decimal? SmrMarginValue { get; init; }
    public decimal OutCostValue { get; init; }
    public decimal EarnedValue { get; init; }
    public decimal? ProgressValue { get; init; }
    public int LateValue { get; init; }
    public decimal? ClosedPercent { get; init; }
    public bool HasExtra => OutCostValue != 0m;
    public bool NegativeEarned => EarnedValue < 0m;
    public bool HasLate => LateValue > 0;
    public bool HasClosedProgress => ClosedPercent.HasValue;
    public bool HasIssuedProgress => ProgressValue.HasValue;
    public double ClosedBarValue => (double)Math.Clamp(ClosedPercent ?? 0m, 0m, 100m);
    public double IssuedBarValue => (double)Math.Clamp(ProgressValue ?? 0m, 0m, 100m);
    public bool ClosedComplete => ClosedPercent >= 99.5m;
    public bool IssuedComplete => ProgressValue >= 99.5m;
    public bool KpOnly { get; init; }
}

/// <summary>
/// Главная страница руководителя (director_view.py): деньги по объектам — договор,
/// сдано по КС, маржа материалов (с покрытием закупа), маржа монтажа, докуплено
/// вне сметы, заработано; строки с неполными данными помечены. Двойной щелчок —
/// открыть объект. Экспорт — CSV (Excel открывает напрямую).
/// </summary>
public partial class DirectorView : UserControl
{
    private readonly string _connectionString;
    private readonly System.Threading.SemaphoreSlim _readGate = new(1, 1);
    private long _loadGeneration;
    private bool _snapshotReady;
    private readonly Dictionary<string, DirectorSnapshot> _snapshots = new();
    private CancellationTokenSource? _readCancellation;
    private CancellationTokenSource? _exportCancellation;
    public event Action<int>? ObjectOpenRequested;

    public DirectorView(string connectionString)
    {
        _connectionString = connectionString;
        InitializeComponent();
        Loaded += (_, _) => _ = ReloadAsync();
        Unloaded += (_, _) =>
        {
            ++_loadGeneration;
            _snapshotReady = false;
            _readCancellation?.Cancel();
            _exportCancellation?.Cancel();
        };
    }

    internal void RequestRefresh()
    {
        _snapshots.Clear();
        if (IsLoaded) _ = ReloadAsync();
    }

    private void DirectorTiles_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateDirectorTileColumns();
    }

    private void UpdateDirectorTileColumns()
    {
        if (DirectorTiles is null || TileClosed is null) return;
        var count = TileClosed.Visibility == Visibility.Collapsed ? 2 : 4;
        var width = DirectorTiles.ActualWidth;
        // A narrow viewport must not squeeze four monetary cards into one row.
        // On wide screens retain the original one-row Python layout.
        var columns = width > 0 && width < 460 ? 1
            : count == 4 && width > 0 && width < 920 ? 2 : count;
        if (DirectorTiles.Columns != columns) DirectorTiles.Columns = columns;
    }

    private async Task ReloadAsync()
    {
        var generation = ++_loadGeneration;
        _readCancellation?.Cancel();
        _exportCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _readCancellation = cancellation;
        var token = cancellation.Token;
        _snapshotReady = false;
        DirectorGrid.ItemsSource = null;
        ExportButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed;
        LoadStatus.Text = "Загрузка сводки из SQL Server…";
        LoadStatusPanel.Visibility = Visibility.Visible;
        TileTotalValue.Text = TileClosedValue.Text = TileExtraValue.Text = TileEarnedValue.Text = "—";
        var mode = (ModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "contracts";
        var kp = mode == "kp";
        HeadingText.Text = kp ? "КП — предварительные просчёты" : "Договоры и группы — в работе";
        TileTotalCaption.Text = kp ? "КП всего, с НДС" : "Договоры всего, с НДС";
        TileEarnedCaption.Text = kp ? "Расчётная прибыль" : "Заработано (материалы + монтаж)";
        TileClosed.Visibility = kp ? Visibility.Collapsed : Visibility.Visible;
        TileExtra.Visibility = kp ? Visibility.Collapsed : Visibility.Visible;
        ClosedColumn.Visibility = TileClosed.Visibility;
        ExtraColumn.Visibility = TileExtra.Visibility;
        IssuedColumn.Visibility = TileClosed.Visibility;
        LateColumn.Visibility = TileClosed.Visibility;
        // Python keeps purchase coverage visible in KP mode.
        CoverageColumn.Visibility = Visibility.Visible;
        UpdateDirectorTileColumns();
        TotalColumn.Header = kp ? "КП, ₽" : "Итого,\n₽";
        EarnedColumn.Header = kp ? "Расчётная\nприбыль" : "Заработано";

        var entered = false;
        try
        {
            await _readGate.WaitAsync(token);
            entered = true;
            if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;
            StartupTrace.Mark("director_load_begin");
            var cached = _snapshots.GetValueOrDefault(mode);
            var data = await Task.Run(() => BuildSnapshot(_connectionString, mode, cached, token), token);
            if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;
            if (data.Day != DateTime.Today)
                throw new InvalidOperationException("Дата изменилась во время загрузки. Обновите сводку.");
            _snapshots[mode] = data;
            DirectorGrid.ItemsSource = data.Rows;
            TileTotalValue.Text = Money.Format(data.Total);
            TileClosedValue.Text = $"{Money.Format(data.Closed)}  ·  {Pct(data.Total != 0m ? data.Closed / data.Total * 100m : null)}";
            TileExtraValue.Text = data.Out != 0m ? Money.Format(data.Out) : "нет";
            TileEarnedValue.Text = Money.Format(data.Earned);
            _snapshotReady = true;
            ExportButton.IsEnabled = _exportCancellation is null;
            LoadStatus.Text = "";
            LoadStatusPanel.Visibility = Visibility.Collapsed;
            StartupTrace.Mark("director_load_end");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || generation != _loadGeneration || !IsLoaded) return;
            _snapshots.Remove(mode);
            LoadStatus.Text = "Не удалось загрузить сводку: " + ex.Message;
            LoadStatusPanel.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
            StartupTrace.Mark("director_load_failed");
        }
        finally
        {
            if (entered) _readGate.Release();
            if (ReferenceEquals(_readCancellation, cancellation)) _readCancellation = null;
        }
    }

    private static string Pct(decimal? v) => v is null ? "—" : $"{v.Value:0}%";
    private sealed record DirectorSnapshot(List<DirectorPreviewRow> Rows,
        decimal Total, decimal Closed, decimal Out, decimal Earned, string Revision, DateTime Day);

    private static DirectorSnapshot BuildSnapshot(string connectionString, string mode,
        DirectorSnapshot? cached, CancellationToken token)
    {
        var snapshot = SqlServerDirector.ReadIfChanged(connectionString, mode,
            cached?.Revision ?? "", cached?.Day, token);
        if (snapshot is null) return cached ?? throw new InvalidOperationException("Нет снимка сводки.");
        var rows = snapshot.Rows;
        var items = new List<DirectorPreviewRow>(rows.Count);
        decimal sumTotal = 0m, sumClosed = 0m, sumOut = 0m, sumEarned = 0m;
        foreach (var d in rows)
        {
            token.ThrowIfCancellationRequested();
            var incompleteCoverage = d.Coverage is { } cov && cov < 95m;
            var missingSmr = d.Smr != 0m && (d.SmrCost ?? 0m) == 0m;
            var incomplete = incompleteCoverage || missingSmr;
            var tips = new List<string>();
            if (incompleteCoverage)
                tips.Add($"Закупочная цена известна только для {d.Coverage:0}% суммы материалов — маржа посчитана по этой части.");
            if (missingSmr) tips.Add("Затраты на монтаж не внесены — маржа на монтаже не посчитана.");
            if (d.KpOnly) tips.Add("Договора ещё нет, показаны цифры КП.");
            var mat = d.MarginPct is null ? "—" : $"{Money.Format(d.Margin)}  ·  {d.MarginPct:0}%";
            var smr = d.SmrMargin is null ? (d.Smr != 0m ? "не внесено" : "—") : Money.Format(d.SmrMargin.Value);
            items.Add(new DirectorPreviewRow
            {
                ObjectId = d.ObjectId,
                Object = (incomplete ? "⚠ " : "") + d.Object + (d.KpOnly ? "   (КП)" : ""),
                Total = Money.Format(d.Total),
                Closed = $"{Money.Format(d.Closed)}  ·  {Pct(d.ClosedPct)}",
                Margin = mat,
                Coverage = Pct(d.Coverage),
                SmrMargin = smr,
                OutCost = d.OutCost != 0m ? Money.Format(d.OutCost) : "—",
                Earned = Money.Format(d.Earned),
                Progress = Pct(d.Progress),
                Late = d.Late > 0 ? d.Late.ToString() : "—",
                DisplayObject = d.Object + (d.KpOnly ? "   (КП)" : ""),
                WarningTip = string.Join("\n", tips),
                Incomplete = incomplete,
                IncompleteCoverage = incompleteCoverage,
                TotalValue = d.Total,
                ClosedValue = d.Closed,
                ClosedPercent = d.Total != 0m ? d.ClosedPct : null,
                MarginValue = d.MarginPct.HasValue ? d.Margin : null,
                CoverageValue = d.Coverage,
                SmrMarginValue = d.SmrMargin,
                OutCostValue = d.OutCost,
                EarnedValue = d.Earned,
                ProgressValue = d.Progress,
                LateValue = d.Late,
                KpOnly = d.KpOnly,
            });
            sumTotal += d.Total;
            sumClosed += d.Closed;
            sumOut += d.OutCost;
            sumEarned += d.Earned;
        }
        return new DirectorSnapshot(items, sumTotal, sumClosed, sumOut, sumEarned, snapshot.Revision, snapshot.Day);
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Первый SelectionChanged приходит из InitializeComponent, когда именованные
        // элементы ещё не созданы; начальные данные загружает Loaded.
        if (!IsLoaded) return;
        _ = ReloadAsync();
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => RequestRefresh();

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Like Python cellDoubleClicked: only the row actually clicked may open.
        // Header, scrollbar and empty background must not open an old selection.
        if (!_snapshotReady || e.ChangedButton != MouseButton.Left ||
            e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(DirectorGrid, source) is not DataGridRow container ||
            container.Item is not DirectorPreviewRow row)
            return;
        ObjectOpenRequested?.Invoke((int)row.ObjectId);
    }

    /// <summary>Экспорт сводки — xlsx, из зафиксированного порядка строк.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!_snapshotReady || _exportCancellation is not null) return;
        var rows = DirectorGrid.Items.OfType<DirectorPreviewRow>().ToArray();
        var title = HeadingText.Text;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        var token = cancellation.Token;
        ExportButton.IsEnabled = false;
        try
        {
            await ContractDocumentSave.XlsxAsync(Window.GetWindow(this), title,
                () => CreateWorkbook(rows, token), token);
        }
        finally
        {
            _exportCancellation = null;
            if (IsLoaded) ExportButton.IsEnabled = _snapshotReady;
        }
    }

    private static XLWorkbook CreateWorkbook(IReadOnlyList<DirectorPreviewRow> rows, CancellationToken token)
    {
        var book = new XLWorkbook();
        try
        {
            var sheet = book.Worksheets.Add("Лист1");
            XlsxExport.Header(sheet, 1, new[] { "Объект", "Договор, ₽", "Сдано по КС", "Маржа: материалы",
                "Закуп известен", "Маржа: монтаж", "Докуплено вне сметы", "Заработано",
                "Выдано на объект", "Просрочено поставок" });
            var row = 2;
            foreach (var rowItem in rows)
            {
                token.ThrowIfCancellationRequested();
                sheet.Cell(row, 1).Value = rowItem.Object;
                sheet.Cell(row, 2).Value = rowItem.Total;
                sheet.Cell(row, 3).Value = rowItem.Closed;
                sheet.Cell(row, 4).Value = rowItem.Margin;
                sheet.Cell(row, 5).Value = rowItem.Coverage;
                sheet.Cell(row, 6).Value = rowItem.SmrMargin;
                sheet.Cell(row, 7).Value = rowItem.OutCost;
                sheet.Cell(row, 8).Value = rowItem.Earned;
                sheet.Cell(row, 9).Value = rowItem.Progress;
                sheet.Cell(row, 10).Value = rowItem.Late;
                row++;
            }
            sheet.SheetView.FreezeRows(1);
            token.ThrowIfCancellationRequested();
            sheet.Columns().AdjustToContents(1, Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 200));
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }
}
