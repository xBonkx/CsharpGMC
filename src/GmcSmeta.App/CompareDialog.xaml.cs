using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Строка предпросмотра сравнения — цвет фона по типу (compare_view LOOK).</summary>
public sealed class ComparePreviewRow
{
    public string Sys { get; init; } = "";
    public string Name { get; init; } = "";
    public string Unit { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Price { get; init; } = "";
    public string SumA { get; init; } = "";
    public string SumB { get; init; } = "";
    public string Diff { get; init; } = "";
    public string RowBackground { get; init; } = "White";
}

/// <summary>
/// «Сравнить документы» (compare_view.py): что изменилось между двумя КП — добавлено,
/// убрано, изменено; плитки итогов, экспорт таблицы в CSV (Excel открывает напрямую).
/// </summary>
public partial class CompareDialog : Window
{
    private readonly string _connectionString;
    private readonly int _currentId;
    private readonly List<(int Id, string Title)> _docs = new();
    private CompareResult? _result;
    private CancellationTokenSource? _loadCancellation;
    private bool _initializing = true;
    private bool _closed;

    public CompareDialog(string connectionString, int contractId, IReadOnlyList<(int Id, string Title)> docs)
    {
        _connectionString = connectionString;
        _currentId = contractId;
        _docs = docs.ToList();
        InitializeComponent();
        foreach (var (id, title) in _docs)
        {
            ComboA.Items.Add(new ComboItem(id, title));
            ComboB.Items.Add(new ComboItem(id, title));
        }
        var me = _docs.FindIndex(d => d.Id == contractId);
        ComboB.SelectedIndex = me;
        ComboA.SelectedIndex = me > 0 ? me - 1 : (_docs.Count > 1 ? 1 : 0);
        ExportButton.IsEnabled = false;
        InfoText.Text = "Загрузка сравнения…";
        _initializing = false;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation?.Cancel();
        };
    }

    private sealed record ComboItem(int Id, string Title)
    {
        public override string ToString() => Title;
    }

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        _initializing = true;
        try
        {
            (ComboA.SelectedIndex, ComboB.SelectedIndex) = (ComboB.SelectedIndex, ComboA.SelectedIndex);
        }
        finally { _initializing = false; }
        await RefreshAsync();
    }

    private async void Combo_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initializing && IsLoaded) await RefreshAsync();
    }

    private void Refresh_Changed(object sender, RoutedEventArgs e)
    {
        // This is a presentation filter, not a new database comparison.
        if (!_initializing && _result is not null) ShowResult();
    }

    private async Task RefreshAsync()
    {
        if (_closed) return;
        _loadCancellation?.Cancel();
        _loadCancellation = null;
        _result = null;
        ExportButton.IsEnabled = false;
        CompareGrid.ItemsSource = null;
        TileMatValue.Text = TileSmrValue.Text = TileTotalValue.Text = TileCountValue.Text = "—";
        if (ComboA.SelectedItem is not ComboItem a || ComboB.SelectedItem is not ComboItem b || a.Id == b.Id)
        {
            InfoText.Text = "Выберите два разных документа.";
            return;
        }

        InfoText.Text = "Загрузка сравнения…";
        using var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;
        try
        {
            var result = await Task.Run(() =>
                SqlServerCompare.Compare(_connectionString, a.Id, b.Id, token), token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            _result = result;
            ShowResult();
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            _result = null;
            ExportButton.IsEnabled = false;
            InfoText.Text = "Не удалось сравнить: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
        }
    }

    private static string Arrow(decimal? a, decimal? b, Func<decimal, string> fmt)
    {
        if (a is null) return fmt(b!.Value);
        if (b is null) return fmt(a.Value);
        return fmt(a.Value) == fmt(b.Value) ? fmt(b.Value) : $"{fmt(a.Value)} → {fmt(b.Value)}";
    }

    private static string Signed(decimal v) => Math.Abs(v) < 0.005m ? ""
        : (v > 0 ? "+" : "−") + Money.Format(Math.Abs(v));

    private static string QtyText(decimal v) => v == decimal.Truncate(v)
        ? v.ToString("0", CultureInfo.CurrentCulture)
        : v.ToString("0.###", CultureInfo.CurrentCulture);

    private void ShowResult()
    {
        var res = _result!;
        TileMatValue.Text = Money.Format(res.MatB) + Delta(res.MatB - res.MatA);
        TileSmrValue.Text = Money.Format(res.SmrB) + Delta(res.SmrB - res.SmrA);
        TileTotalValue.Text = Money.Format(res.TotalB) + Delta(res.TotalB - res.TotalA);
        TileCountValue.Text = $"+{res.Added}  −{res.Removed}  ~{res.ChangedCount}";

        var rows = new ObservableCollection<ComparePreviewRow>();
        var showSame = ShowSameCheck.IsChecked == true;
        var shown = 0;
        string? currentSystem = null;
        foreach (var r in res.Rows)
        {
            if (r.Kind == "same" && !showSame) continue;
            var (fg, bg, word) = r.Kind switch
            {
                "added" => ("#047857", "#ECFDF3", "добавлено"),
                "removed" => ("#B91C1C", "#FEF2F2", "убрано"),
                "changed" => ("#B45309", "#FFFBEB", "изменено"),
                _ => ("", "White", ""),
            };
            var name = r.Name;
            if (r.Changed.Contains("sys")) name += $"   (было в {r.SysWas})";
            rows.Add(new ComparePreviewRow
            {
                Sys = r.Sys,
                Name = name,
                Unit = r.Unit,
                Qty = Arrow(r.QtyA, r.QtyB, QtyText),
                Price = Arrow(r.PriceA, r.PriceB, Money.Format),
                SumA = r.QtyA is null ? "" : Money.Format(r.SumA),
                SumB = r.QtyB is null ? "" : Money.Format(r.SumB),
                Diff = Signed(r.Diff),
                RowBackground = bg,
            });
            shown++;
        }
        if (shown == 0)
            rows.Add(new ComparePreviewRow
            {
                Name = "Отличий нет — документы совпадают по строкам, количествам и ценам.",
            });
        CompareGrid.ItemsSource = rows;
        var d = res.TotalB - res.TotalA;
        InfoText.Text = $"Итого было {Money.Format(res.TotalA)} ₽, стало {Money.Format(res.TotalB)} ₽" +
            (Math.Abs(d) >= 0.005m ? $"  ({Signed(d)} ₽)" : "  (без изменений)");
    }

    private static string Delta(decimal d) => Math.Abs(d) < 0.005m ? "" : "   " + Signed(d);

    /// <summary>Экспорт таблицы сравнения — xlsx (compare_view.export).</summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var res = _result;
        if (res is null) return;
        XlsxExport.Save(this, $"Сравнение — {res.TitleB}", sheet =>
        {
            sheet.Cell(1, 1).Value = "Было: " + res.TitleA;
            sheet.Cell(2, 1).Value = "Стало: " + res.TitleB;
            sheet.Range(1, 1, 2, 1).Style.Font.Bold = true;
            XlsxExport.Header(sheet, 4, new[] { "Система", "Наименование", "Ед.", "Кол-во было", "Кол-во стало",
                "Цена было", "Цена стало", "Сумма было", "Сумма стало", "Разница", "Что" });
            var row = 5;
            foreach (var r in res.Rows)
            {
                var word = r.Kind switch
                {
                    "added" => "добавлено", "removed" => "убрано", "changed" => "изменено", _ => "",
                };
                sheet.Cell(row, 1).Value = r.Sys;
                sheet.Cell(row, 2).Value = r.Name;
                sheet.Cell(row, 3).Value = r.Unit;
                sheet.Cell(row, 4).Value = r.QtyA is null ? "" : (double)r.QtyA;
                sheet.Cell(row, 5).Value = r.QtyB is null ? "" : (double)r.QtyB;
                sheet.Cell(row, 6).Value = r.PriceA is null ? "" : (double)r.PriceA;
                sheet.Cell(row, 7).Value = r.PriceB is null ? "" : (double)r.PriceB;
                sheet.Cell(row, 8).Value = r.QtyA is null ? "" : (double)r.SumA;
                sheet.Cell(row, 9).Value = r.QtyB is null ? "" : (double)r.SumB;
                sheet.Cell(row, 10).Value = r.Diff == 0m ? "" : (double)r.Diff;
                sheet.Cell(row, 11).Value = word;
                sheet.Range(row, 6, row, 10).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            row++;
            foreach (var (label, a, b) in new[] { ("Материалы", res.MatA, res.MatB),
                ("СМР", res.SmrA, res.SmrB), ("ИТОГО с НДС", res.TotalA, res.TotalB) })
            {
                sheet.Cell(row, 2).Value = label;
                sheet.Cell(row, 2).Style.Font.Bold = true;
                sheet.Cell(row, 8).Value = (double)a;
                sheet.Cell(row, 9).Value = (double)b;
                sheet.Cell(row, 10).Value = (double)(b - a);
                sheet.Range(row, 8, row, 10).Style.NumberFormat.Format = "#,##0.00";
                sheet.Range(row, 8, row, 10).Style.Font.Bold = true;
                row++;
            }
            sheet.SheetView.FreezeRows(4);
        });
    }
}
