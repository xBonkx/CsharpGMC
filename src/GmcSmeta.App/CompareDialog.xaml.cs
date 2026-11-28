using System.Windows;
using ClosedXML.Excel;
using Microsoft.Win32;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

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
    private CompareDisplaySnapshot? _display;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _exportCancellation;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private bool _exportBusy;
    private bool _publishingExport;
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
        Closing += (_, e) => { if (_publishingExport) e.Cancel = true; };
        Closed += (_, _) =>
        {
            _closed = true;
            _loadCancellation?.Cancel();
            _exportCancellation?.Cancel();
        };
    }

    private sealed record ComboItem(int Id, string Title)
    {
        public override string ToString() => Title;
    }

    private async void Swap_Click(object sender, RoutedEventArgs e)
    {
        if (_publishingExport) return;
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
        if (!_initializing && !_publishingExport && IsLoaded) await RefreshAsync();
    }

    private void Refresh_Changed(object sender, RoutedEventArgs e)
    {
        // This is a presentation filter, not a new database comparison.
        if (!_initializing && _result is not null) ShowResult();
    }

    private async Task RefreshAsync()
    {
        if (_closed || _publishingExport) return;
        _loadCancellation?.Cancel();
        _exportCancellation?.Cancel();
        _loadCancellation = null;
        _result = null;
        _display = null;
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
            var snapshot = await Task.Run(async () =>
            {
                await _readGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var result = SqlServerCompare.Compare(_connectionString, a.Id, b.Id, token);
                    return (Result: result, Display: CompareDisplayProjection.Build(result, token));
                }
                finally { _readGate.Release(); }
            }, token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            _result = snapshot.Result;
            _display = snapshot.Display;
            ShowResult();
            ExportButton.IsEnabled = !_exportBusy;
        }
        catch (Exception ex)
        {
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            _result = null;
            _display = null;
            ExportButton.IsEnabled = false;
            InfoText.Text = "Не удалось сравнить: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
        }
    }

    private void ShowResult()
    {
        if (_display is not { } display) return;
        TileMatValue.Text = display.Materials;
        TileSmrValue.Text = display.Smr;
        TileTotalValue.Text = display.Total;
        TileCountValue.Text = display.Count;
        CompareGrid.ItemsSource = ShowSameCheck.IsChecked == true ? display.AllRows : display.ChangedRows;
        InfoText.Text = display.Info;
    }

    /// <summary>Export the captured comparison snapshot, not a later combo selection.</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _exportBusy || _result is not { } res) return;
        var picker = new SaveFileDialog
        {
            Title = "Сохранить в Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = XlsxExport.SanitizeFileName($"Сравнение — {res.TitleB}")
        };
        if (picker.ShowDialog(this) != true || _closed || !ReferenceEquals(res, _result)) return;
        var path = picker.FileName;
        _exportBusy = true;
        ExportButton.IsEnabled = false;
        using var cancellation = new CancellationTokenSource();
        _exportCancellation = cancellation;
        var token = cancellation.Token;
        ExportFilePublication.PreparedExport? prepared = null;
        try
        {
            prepared = await Task.Run(() => ExportFilePublication.Stage(path, stream =>
            {
                using var workbook = new XLWorkbook();
                var sheet = workbook.Worksheets.Add("Лист1");
                FillComparisonSheet(sheet, res, token);
                token.ThrowIfCancellationRequested();
                sheet.Columns().AdjustToContents(1, Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 200));
                token.ThrowIfCancellationRequested();
                workbook.SaveAs(stream);
                token.ThrowIfCancellationRequested();
            }), token);
            if (_closed || token.IsCancellationRequested || !ReferenceEquals(res, _result)) return;
            // Publication is the commit boundary. Keep the captured comparison
            // and window alive until the potentially slow UNC rename completes.
            _publishingExport = true;
            ComboA.IsEnabled = ComboB.IsEnabled = SwapButton.IsEnabled = false;
            await Task.Run(() => prepared.Publish());
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                { UseShellExecute = true });
            }
            catch { /* Excel viewer is optional. */ }
        }
        catch (Exception ex)
        {
            if (!_closed && !token.IsCancellationRequested && ReferenceEquals(res, _result))
                MessageBox.Show(this, ex.Message, "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            try
            {
                if (prepared is not null) await Task.Run(prepared.Dispose);
            }
            finally
            {
                _publishingExport = false;
                _exportCancellation = null;
                _exportBusy = false;
                if (!_closed)
                {
                    ComboA.IsEnabled = ComboB.IsEnabled = SwapButton.IsEnabled = true;
                    ExportButton.IsEnabled = _result is not null;
                }
            }
        }
    }

    private static void FillComparisonSheet(IXLWorksheet sheet, CompareResult res, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        sheet.Cell(1, 1).Value = "Было: " + res.TitleA;
        sheet.Cell(2, 1).Value = "Стало: " + res.TitleB;
        sheet.Range(1, 1, 2, 1).Style.Font.Bold = true;
        XlsxExport.Header(sheet, 4, new[] { "Система", "Наименование", "Ед.", "Кол-во было", "Кол-во стало",
            "Цена было", "Цена стало", "Сумма было", "Сумма стало", "Разница", "Что" });
        var row = 5;
        foreach (var r in res.Rows)
        {
            token.ThrowIfCancellationRequested();
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
            row++;
        }
        if (row > 5) sheet.Range(5, 6, row - 1, 10).Style.NumberFormat.Format = "#,##0.00";
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
    }
}
