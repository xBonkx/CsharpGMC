using System.Globalization;
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
            var (values, revision) = await Task.Run(() =>
                SqlServerKsRequisites.ReadSnapshot(_connectionString, _contractId));
            if (!IsLoaded) return;
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
            SaveButton.IsEnabled = true;
            StatusText.Text = "Изменения сохраняются в тестовой копии SQL Server.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Ошибка загрузки: " + ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_loaded is null || string.IsNullOrWhiteSpace(_revision) || !SaveButton.IsEnabled) return;
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
        SaveButton.IsEnabled = false;
        StatusText.Text = "Сохранение…";
        try
        {
            var result = await Task.Run(() =>
                SqlServerKsRequisites.Save(_connectionString, _contractId, values, revision));
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
            StatusText.Text = "Сохранение не подтверждено: " + ex.Message +
                ". Откройте окно заново и проверьте данные.";
        }
        finally { SaveButton.IsEnabled = !string.IsNullOrWhiteSpace(_revision); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
