using System.Windows;
using System.IO;
using System.Diagnostics;
using Microsoft.Win32;
using ClosedXML.Excel;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Actual full amount from every KS-2 in selected KS-3 group.</summary>
public partial class KsGroupReportWindow : Window
{
    private readonly string _connectionString;
    private readonly long _contractId;
    private readonly string _groupKey;
    private SqlServerKsPrintData.Package? _package;
    private bool _exportInProgress;
    private bool _closed;

    public KsGroupReportWindow(string connectionString, long contractId, string groupKey)
    {
        _connectionString=connectionString;
        _contractId=contractId;
        _groupKey=groupKey;
        InitializeComponent();
        TitleText.Text="КС-3 № " + groupKey;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StatusText.Text="Чтение актов, строк КС и реквизитов из SQL Server…";
        try
        {
            var package=await Task.Run(() => SqlServerKsPrintData.Read(
                _connectionString,_contractId,_groupKey));
            if (_closed || !IsLoaded) return;
            _package=package;
            var r=package.Report;
            TitleText.Text="КС-3 № " + r.Key;
            SubtitleText.Text="Договор № " + r.AgreementNumber +
                " · " + r.Date.ToString("dd.MM.yyyy") + " · период " +
                r.PeriodFrom?.ToString("dd.MM.yyyy") + " — " +
                r.PeriodTo?.ToString("dd.MM.yyyy");
            CountText.Text=r.Acts.Count.ToString();
            PeriodText.Text=Money.Format(r.PeriodNet);
            YearAmountLabel.Text=r.GrossCumulative ? "С начала года с НДС (начальный остаток)" : "С начала года без НДС";
            StartAmountLabel.Text=r.GrossCumulative ? "С начала работ с НДС (начальный остаток)" : "С начала работ без НДС";
            YearText.Text=Money.Format(r.YearNet);
            StartText.Text=Money.Format(r.SinceStartNet);
            GrossText.Text=Money.Format(r.PeriodGross);
            VatText.Text="НДС за период: "+Money.Format(r.PeriodVat)+" ₽";
            ActGrid.ItemsSource=r.Acts;
            PreviousGrid.ItemsSource=r.Previous;
            StatusText.Text=$"Группа из {r.Acts.Count} КС-2, " +
                $"{package.Scope.Documents.Count} документ(ов) договора. " +
                $"Прочитано {package.Lines.Count} строк.";
            ExcelButton.IsEnabled=true;
            VorButton.IsEnabled=true;
            PdfButton.IsEnabled=true;
        }
        catch(Exception ex)
        {
            if (!_closed) StatusText.Text="Не удалось прочитать КС-3: "+ex.Message;
        }
    }

    private async void Excel_Click(object sender, RoutedEventArgs e)
    {
        if (_package is not { } p) return;
        await ExportFileAsync("КС-2 и КС-3 №" + p.Report.Key, false,
            path => SaveWorkbook(path, () => KsCustomerForms.Create(p)));
    }

    private async void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_package is not { } p) return;
        await ExportFileAsync("КС-2 + КС-3 №" + p.Report.Key, true, path =>
        {
            var pdf = KsPdfForms.Create(p);
            if (pdf.Length < 10 || pdf[0] != '%' || pdf[1] != 'P' || pdf[2] != 'D' || pdf[3] != 'F')
                throw new InvalidDataException("PDF не сформирован.");
            File.WriteAllBytes(path, pdf);
        });
    }

    private async void Vor_Click(object sender, RoutedEventArgs e)
    {
        if (_package is not { } p) return;
        var act = ActGrid.SelectedItem as SqlServerActs.ActInfo
            ?? p.Report.Acts.FirstOrDefault();
        if (act is null) return;
        await ExportFileAsync("ВОР КС-2 №" + act.Number, false,
            path => SaveWorkbook(path, () => KsVorForms.Create(p, act.Id)));
    }

    private static void SaveWorkbook(string path, Func<XLWorkbook> create)
    {
        using var workbook = create();
        if (workbook.Worksheets.Count == 0)
            throw new InvalidOperationException("В книге нет листов.");
        workbook.SaveAs(path);
    }

    private void SetExportButtons(bool enabled)
    {
        ExcelButton.IsEnabled = enabled;
        PdfButton.IsEnabled = enabled;
        VorButton.IsEnabled = enabled;
    }

    // The preview package and selected act are captured on the UI thread.
    // Rendering never touches the visual tree or re-reads a different act.
    private async Task ExportFileAsync(string name, bool pdf, Action<string> render)
    {
        if (_closed || _exportInProgress) return;
        _exportInProgress = true;
        SetExportButtons(false);
        string? temporary = null;
        try
        {
            var invalid = Path.GetInvalidFileNameChars();
            var dialog = new SaveFileDialog
            {
                Title = pdf ? "Сохранить КС в PDF" : "Сохранить в Excel",
                Filter = pdf ? "PDF (*.pdf)|*.pdf" : "Excel (*.xlsx)|*.xlsx",
                FileName = string.Concat(name.Select(c => invalid.Contains(c) ? (pdf ? '_' : ' ') : c)).Trim()
            };
            if (dialog.ShowDialog(this) != true || _closed) return;
            var destination = Path.GetFullPath(dialog.FileName);
            var folder = Path.GetDirectoryName(destination)!;
            temporary = Path.Combine(folder, ".gmc-ks-group-" + Guid.NewGuid().ToString("N") +
                (pdf ? ".pdf" : ".xlsx"));
            var staged = temporary;
            StatusText.Text = "Формирование файла…";
            var timer = Stopwatch.StartNew();
            await Task.Run(() => render(staged));
            var renderMs = timer.ElapsedMilliseconds;
            if (_closed) return;

            // Commit point: until here an existing destination is untouched.
            timer.Restart();
            await Task.Run(() => File.Move(staged, destination, overwrite: true));
            temporary = null;
            Trace.WriteLine($"KS group export: render+stage={renderMs}ms; publish={timer.ElapsedMilliseconds}ms");
            if (_closed) return;
            StatusText.Text = "Сохранено: " + Path.GetFileName(destination);
            try { Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true }); }
            catch { /* Viewer is optional. */ }
        }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = "Ошибка экспорта: " + ex.Message;
            else Trace.WriteLine("KS group export failed after close: " + ex.GetType().Name);
        }
        finally
        {
            if (temporary is not null)
            {
                var staged = temporary;
                try { await Task.Run(() => File.Delete(staged)); }
                catch { /* Preserve original error. */ }
            }
            _exportInProgress = false;
            if (!_closed) SetExportButtons(_package is not null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
