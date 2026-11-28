using System.ComponentModel;
using System.Globalization;
using System.Threading;
using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Editable SQL Server KS requisites with optimistic revision guard.</summary>
public partial class KsRequisitesWindow : Window
{
    private readonly string _connectionString;
    private readonly long _contractId;
    private string _revision = "";
    private SqlServerKsRequisites.Values? _loaded;
    private bool _saving;
    private bool _closed;
    private readonly CancellationTokenSource _readLifetime = new();

    public KsRequisitesWindow(string connectionString, long contractId)
    {
        _connectionString = connectionString;
        _contractId = contractId;
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        StatusText.Text = "Чтение реквизитов SQL Server…";
        try
        {
            var token = _readLifetime.Token;
            var (values, revision) = await Task.Run(() =>
                SqlServerKsRequisites.ReadSnapshot(_connectionString, _contractId, token), token);
            if (_closed || !IsLoaded) return;
            _loaded = values;
            _revision = revision;
            ObjectTitle.Text = values.ObjectName;
            CustomerFullBox.Text = values.CustomerFull;
            SiteFullBox.Text = values.SiteFull;
            SitePartBox.Text = values.SitePart;
            CustOrgBox.Text = values.CustomerOrganization;
            CustPostBox.Text = values.CustomerPosition;
            CustNameBox.Text = values.CustomerName;
            SecondRoleBox.Text = values.SecondRole;
            SecondOrgBox.Text = values.SecondOrganization;
            SecondPostBox.Text = values.SecondPosition;
            SecondNameBox.Text = values.SecondName;
            WorkTitleBox.Text = values.WorkTitle;
            WorksStartBox.Text = values.WorksStart;
            RequisitesFields.IsEnabled = !string.IsNullOrWhiteSpace(_revision);
            SaveButton.IsEnabled = RequisitesFields.IsEnabled;
            StatusText.Text = "Изменения сохраняются в тестовой копии SQL Server.";
        }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = "Ошибка загрузки: " + ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_closed || _saving || _loaded is null || string.IsNullOrWhiteSpace(_revision) || !SaveButton.IsEnabled) return;
        var start = WorksStartBox.Text.Trim();
        if (start.Length > 0 && !DateTime.TryParseExact(start, "dd.MM.yyyy",
                CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out _))
        {
            StatusText.Text = "Дата начала работ должна быть в формате дд.мм.гггг.";
            return;
        }
        var values = _loaded with
        {
            CustomerFull = CustomerFullBox.Text.Trim(),
            SiteFull = SiteFullBox.Text.Trim(),
            SitePart = SitePartBox.Text.Trim(),
            CustomerOrganization = CustOrgBox.Text.Trim(),
            CustomerPosition = CustPostBox.Text.Trim(),
            CustomerName = CustNameBox.Text.Trim(),
            SecondRole = SecondRoleBox.Text.Trim(),
            SecondOrganization = SecondOrgBox.Text.Trim(),
            SecondPosition = SecondPostBox.Text.Trim(),
            SecondName = SecondNameBox.Text.Trim(),
            WorkTitle = WorkTitleBox.Text.Trim(),
            WorksStart = start
        };
        var revision = _revision;
        _saving = true;
        RequisitesFields.IsEnabled = false;
        SaveButton.IsEnabled = false;
        StatusText.Text = "Сохранение…";
        try
        {
            var result = await Task.Run(() =>
                SqlServerKsRequisites.Save(_connectionString, _contractId, values, revision));
            if (_closed) return;
            if (!result.Saved)
            {
                _revision = "";
                StatusText.Text = (result.Error ?? "Не удалось сохранить реквизиты.") +
                    " Откройте окно заново перед повторной записью.";
                return;
            }
            _revision = result.Revision;
            _loaded = values;
            StatusText.Text = "Реквизиты сохранены.";
        }
        catch (Exception ex)
        {
            _revision = "";
            if (_closed) return;
            StatusText.Text = "Сохранение не подтверждено: " + ex.Message +
                ". Откройте окно заново и проверьте данные.";
        }
        finally
        {
            _saving = false;
            if (!_closed)
            {
                RequisitesFields.IsEnabled = !string.IsNullOrWhiteSpace(_revision);
                SaveButton.IsEnabled = RequisitesFields.IsEnabled;
            }
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_saving)
        {
            e.Cancel = true;
            StatusText.Text = "Дождитесь подтверждения сохранения реквизитов.";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _readLifetime.Cancel();
        _readLifetime.Dispose();
        base.OnClosed(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
