using System.Windows;
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
            if (!IsLoaded) return;
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
            StatusText.Text="Не удалось прочитать КС-3: "+ex.Message;
        }
    }

    private void Excel_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null) return;
        var p=_package;
        XlsxExport.SaveCreatedWorkbook(this,
            "КС-2 и КС-3 №" + p.Report.Key,
            () => KsCustomerForms.Create(p));
    }

    private void Pdf_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null) return;
        KsPdfForms.Save(this, "КС-2 + КС-3 №" + _package.Report.Key,
            () => KsPdfForms.Create(_package));
    }

    private void Vor_Click(object sender, RoutedEventArgs e)
    {
        if (_package is null) return;
        var act = ActGrid.SelectedItem as SqlServerActs.ActInfo
            ?? _package.Report.Acts.FirstOrDefault();
        if (act is null) return;
        XlsxExport.SaveCreatedWorkbook(this,
            "ВОР КС-2 №" + act.Number,
            () => KsVorForms.Create(_package, act.Id));
    }

    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
