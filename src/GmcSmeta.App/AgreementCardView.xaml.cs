using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class AgreementCardView : UserControl
{
    private readonly string _connection;
    private readonly int _objectId;
    private SqlServerWorkspace.AgreementCardSnapshot? _snapshot;
    private bool _saving;
    public event Action? BackRequested;
    public event Action? Saved;
    public event Action<DocRow>? EditDocumentRequested;

    public AgreementCardView(string connection, int objectId)
    {
        _connection = connection;
        _objectId = objectId;
        InitializeComponent();
        Loaded += LoadOnce;
    }

    private async void LoadOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadOnce;
        StatusText.Text = "Загрузка реквизитов…";
        try
        {
            var snapshot = await Task.Run(() => SqlServerWorkspace.ReadAgreementCard(_connection, _objectId));
            if (!IsLoaded) return;
            _snapshot = snapshot;
            var p = snapshot.Properties;
            var v = snapshot.Print;
            TitleText.Text = p.Name;
            var agreement = snapshot.Agreement;
            AgreementNumberBox.Text = agreement?.Number ?? "Не определён";
            AgreementDateBox.Text = agreement?.Date ?? "Не определена";
            CompanyBox.Text = snapshot.Contractor.Length == 0 ? "Не определён" : snapshot.Contractor;
            AgreementText.Text = agreement is null
                ? "Единые реквизиты не подтверждены. Проверьте номера, даты, подрядчиков и основания ДС в списке ниже."
                : "Общие номер, дата и подрядчик подтверждены реквизитами приложений.";
            SourceGrid.ItemsSource = snapshot.Documents;
            SourceExpander.IsExpanded = agreement is null;
            SourceExpander.Visibility = snapshot.Documents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (snapshot.Documents.Count == 0)
                AgreementText.Text = "Приложений пока нет. Самостоятельные юридические реквизиты карточки ещё не поддерживаются.";
            NameBox.Text = p.Name;
            CustomerBox.Text = p.Customer;
            AddressBox.Text = p.Address;
            FolderBox.Text = p.Folder;
            NoteBox.Text = p.Note;
            TagBox.ItemsSource = snapshot.Tags;
            TagBox.SelectedValue = p.Form;
            CustomerFullBox.Text = v.CustomerFull;
            SiteFullBox.Text = v.SiteFull;
            CustOrgBox.Text = v.CustomerOrganization;
            CustPostBox.Text = v.CustomerPosition;
            CustNameBox.Text = v.CustomerName;
            SecondRoleBox.Text = v.SecondRole;
            SecondOrgBox.Text = v.SecondOrganization;
            SecondPostBox.Text = v.SecondPosition;
            SecondNameBox.Text = v.SecondName;
            WorkTitleBox.Text = v.WorkTitle;
            WorksStartBox.Text = v.WorksStart;
            Fields.IsEnabled = SaveButton.IsEnabled = true;
            StatusText.Text = "Изменения вступят в силу после сохранения. Переход на другую карточку отменяет несохранённые правки.";
        }
        catch (Exception ex) { if (IsLoaded) StatusText.Text = "Не удалось загрузить: " + ex.Message; }
    }

    private ObjectProperties ReadProperties() => new(NameBox.Text, CustomerBox.Text, AddressBox.Text,
        FolderBox.Text, NoteBox.Text, TagBox.SelectedValue as string ?? "");

    private SqlServerKsRequisites.Values ReadPrint(SqlServerKsRequisites.Values v) => v with
    {
        CustomerFull = CustomerFullBox.Text, SiteFull = SiteFullBox.Text,
        CustomerOrganization = CustOrgBox.Text, CustomerPosition = CustPostBox.Text, CustomerName = CustNameBox.Text,
        SecondRole = SecondRoleBox.Text, SecondOrganization = SecondOrgBox.Text,
        SecondPosition = SecondPostBox.Text, SecondName = SecondNameBox.Text,
        WorkTitle = WorkTitleBox.Text, WorksStart = WorksStartBox.Text
    };

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _snapshot is not { } snapshot || !SaveButton.IsEnabled) return;
        var properties = ReadProperties();
        var print = ReadPrint(snapshot.Print);
        try
        {
            SqlServerWorkspace.ValidateObject(properties);
            SqlServerWorkspace.ValidateSharedPrint(_objectId, print);
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        _saving = true;
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        StatusText.Text = "Сохранение…";
        try
        {
            await Task.Run(() => SqlServerWorkspace.SaveAgreementCard(_connection, _objectId, properties, print, snapshot.Revision));
        }
        catch (Exception ex)
        {
            if (IsLoaded) StatusText.Text = "Сохранение не подтверждено: " + ex.Message + ". Вернитесь к карточке и откройте реквизиты заново.";
            return;
        }
        finally { _saving = false; }
        if (IsLoaded) Saved?.Invoke();
    }

    private bool HasUnsavedChanges() => _snapshot is { } snapshot &&
        (ReadProperties() != snapshot.Properties || ReadPrint(snapshot.Print) != snapshot.Print);

    private void EditDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || sender is not Button { DataContext: SqlServerWorkspace.AgreementCardDocument { CanEdit: true } source }) return;
        if (HasUnsavedChanges())
        {
            StatusText.Text = "Сначала сохраните общие реквизиты или вернитесь к карточке без сохранения.";
            return;
        }
        EditDocumentRequested?.Invoke(source.Document);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        if (HasUnsavedChanges() &&
            MessageBox.Show(Window.GetWindow(this), "Вернуться без сохранения изменений?", "Реквизиты",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        BackRequested?.Invoke();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка документов", Multiselect = false };
        if (System.IO.Path.IsPathFullyQualified(FolderBox.Text) && !FolderBox.Text.StartsWith(@"\\"))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }
}
