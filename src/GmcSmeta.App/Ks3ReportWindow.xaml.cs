using System.Windows;
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
    private SqlServerActs.Ks3Report? _report;

    public Ks3ReportWindow(string connectionString, long actId, string actNumber,
        DateTime actDate, DateTime? from, DateTime? to)
    {
        _connectionString = connectionString;
        _actId = actId;
        _actNumber = actNumber;
        _actDate = actDate;
        InitializeComponent();
        ActInfoText.Text = $"КС-2 № {actNumber} от {actDate:dd.MM.yyyy} · " +
            $"период с {from:dd.MM.yyyy} по {to:dd.MM.yyyy}";
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            ActInfoText.Text += " · загрузка SQL Server…";
            var report = await Task.Run(() => SqlServerActs.Ks3Figures(_connectionString, _actId));
            if (!IsLoaded) return;
            _report = report;
            ActInfoText.Text = $"КС-2 № {_actNumber} от {_actDate:dd.MM.yyyy}";
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
            ActInfoText.Text = "Ошибка загрузки: " + ex.Message;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var report = _report;
        if (report is null) return;
        XlsxExport.Save(this, $"Расчёт КС-3 № {_actNumber} от {_actDate:dd.MM.yyyy}", sheet =>
        {
            sheet.Cell(1, 1).Value = "РАСЧЁТ КС-3 — финансовая справка, не официальный бланк";
            sheet.Range(1, 1, 1, 3).Merge();
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(2, 1).Value = $"КС-2 № {_actNumber} от {_actDate:dd.MM.yyyy}";
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
        });
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
