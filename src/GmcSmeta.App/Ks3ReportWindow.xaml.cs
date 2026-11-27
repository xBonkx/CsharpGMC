using System.Threading;
using System.Windows;
using ClosedXML.Excel;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Calculated KS-3 overview for a selected act. It is explicitly NOT a
/// regulatory-print KS-3 document; it can export the underlying financial
/// figures for further review in Excel.
/// </summary>
public partial class Ks3ReportWindow : Window
{
    public sealed record PreviousActRow(string Title, decimal Net);

    private readonly string _connectionString;
    private readonly long _actId;
    private readonly string _actNumber;
    private readonly DateTime _actDate;
    private readonly string _actInfo;
    private SqlServerActs.Ks3Report? _report;
    private readonly CancellationTokenSource _exportLifetime = new();
    private bool _closed;
    private bool _exportInProgress;

    public Ks3ReportWindow(string connectionString, long actId, string actNumber,
        DateTime actDate, DateTime? from, DateTime? to)
    {
        _connectionString = connectionString;
        _actId = actId;
        _actNumber = actNumber;
        _actDate = actDate;
        InitializeComponent();
        _actInfo = $"КС-2 № {actNumber} от {actDate:dd.MM.yyyy} · " +
            $"период с {from:dd.MM.yyyy} по {to:dd.MM.yyyy}";
        ActInfoText.Text = _actInfo;
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _closed = true;
            _exportLifetime.Cancel();
            _exportLifetime.Dispose();
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            ActInfoText.Text += " · загрузка SQL Server…";
            var token = _exportLifetime.Token;
            var report = await Task.Run(() => SqlServerActs.Ks3Figures(_connectionString, _actId), token);
            if (_closed || !IsLoaded) return;
            _report = report;
            ActInfoText.Text = _actInfo;
            PeriodNetText.Text = Money.Format(report.Period);
            YearAmountLabel.Text = report.GrossCumulative ? "С начала года · с НДС, включая начальный остаток" : "С начала года · без НДС";
            StartAmountLabel.Text = report.GrossCumulative ? "С начала работ · с НДС, включая начальный остаток" : "С начала работ · без НДС";
            YearNetText.Text = Money.Format(report.Year);
            StartNetText.Text = Money.Format(report.Start);
            VatText.Text = Money.Format(report.Vat);
            TotalText.Text = Money.Format(report.Total);
            PreviousGrid.ItemsSource = report.Previous
                .Select(r => new PreviousActRow(r.Title, r.Net)).ToList();
            ExportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (!_closed) ActInfoText.Text = "Ошибка загрузки: " + ex.Message;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var report = _report;
        if (_closed || _exportInProgress || report is null) return;
        _exportInProgress = true;
        ExportButton.IsEnabled = false;
        var originalInfo = ActInfoText.Text;
        var token = _exportLifetime.Token;
        try
        {
            ActInfoText.Text = originalInfo + " · подготовка Excel…";
            var saved = await ContractDocumentSave.XlsxAsync(this,
                $"Расчёт КС-3 № {_actNumber} от {_actDate:dd.MM.yyyy}",
                () => CreateWorkbook(report, _actNumber, _actDate), token);
            if (!_closed)
                ActInfoText.Text = originalInfo + (saved ? " · Excel сохранён" : " · экспорт не завершён");
        }
        catch (Exception ex)
        {
            if (!_closed) ActInfoText.Text = originalInfo + " · ошибка экспорта: " + ex.Message;
        }
        finally
        {
            _exportInProgress = false;
            if (!_closed) ExportButton.IsEnabled = _report is not null;
        }
    }

    private static XLWorkbook CreateWorkbook(SqlServerActs.Ks3Report report, string number, DateTime date)
    {
        var book = new XLWorkbook();
        try
        {
            var sheet = book.Worksheets.Add("Лист1");
            sheet.Cell(1, 1).Value = "РАСЧЁТ КС-3 — финансовая справка, не официальный бланк";
            sheet.Range(1, 1, 1, 3).Merge();
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).Value = $"КС-2 № {number} от {date:dd.MM.yyyy}";
            XlsxExport.Header(sheet, 4, new[] { "Показатель", "Сумма, ₽" });
            var amounts = new (string Caption, decimal Value)[]
            {
                ("За период без НДС", report.Period),
                (report.GrossCumulative ? "С начала года с НДС (включая начальный остаток)" : "С начала года без НДС", report.Year),
                (report.GrossCumulative ? "С начала работ с НДС (включая начальный остаток)" : "С начала работ без НДС", report.Start),
                ("НДС за период", report.Vat),
                ("Итого за период с НДС", report.Total)
            };
            int row = 5;
            foreach (var (caption, amount) in amounts)
            {
                sheet.Cell(row, 1).Value = caption;
                sheet.Cell(row, 2).Value = (double)amount;
                sheet.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            row += 2;
            XlsxExport.Header(sheet, row++, new[] { "Предыдущие акты КС-2", "Без НДС, ₽" });
            foreach (var (title, net) in report.Previous)
            {
                sheet.Cell(row, 1).Value = title;
                sheet.Cell(row, 2).Value = (double)net;
                sheet.Cell(row, 2).Style.NumberFormat.Format = "#,##0.00";
                row++;
            }
            sheet.Column(1).Width = 57;
            sheet.Column(2).Width = 24;
            // XlsxExport.Save previously applied the same bounded autofit.
            sheet.Columns().AdjustToContents(1, Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 200));
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
