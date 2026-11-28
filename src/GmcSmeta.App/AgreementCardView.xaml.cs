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
    private readonly long? _initialLegalId;
    private SqlServerWorkspace.AgreementCardSnapshot? _snapshot;
    private bool _saving;
    private bool _loadingCard;
    public event Action? BackRequested;
    public event Action? Saved;
    public event Action<DocRow>? EditDocumentRequested;

    public AgreementCardView(string connection, int objectId, long? legalId = null)
    {
        _connection = connection;
        _objectId = objectId;
        _initialLegalId = legalId;
        InitializeComponent();
        Loaded += LoadOnce;
    }

    private async void LoadOnce(object sender, RoutedEventArgs e)
    {
        Loaded -= LoadOnce;
        await LoadCard(_initialLegalId);
    }

    private async Task<bool> LoadCard(long? legalId)
    {
        if (_loadingCard) return false;
        _loadingCard = true;
        _snapshot = null; // never retain writable stale identity after failed load
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        StatusText.Text = "Загрузка реквизитов…";
        try
        {
            var snapshot = await Task.Run(() => SqlServerWorkspace.ReadAgreementCard(_connection, _objectId, legalId));
            if (!IsLoaded) return false;
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
            if (snapshot.Documents.Count == 0 && !snapshot.LegalEnabled)
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
            InitializeLegal(snapshot);
            Fields.IsEnabled = true;
            SaveButton.IsEnabled = !snapshot.RequiresLegalSelection;
            SetSelectionOnly(snapshot.RequiresLegalSelection);
            StatusText.Text = snapshot.RequiresLegalSelection
                ? "Выберите юридический договор или явно создайте новый. Сохранение пока недоступно."
                : "Изменения вступят в силу после сохранения. Переход на другую карточку отменяет несохранённые правки.";
            return true;
        }
        catch (Exception ex)
        {
            _snapshot = null;
            Fields.IsEnabled = SaveButton.IsEnabled = false;
            if (IsLoaded) StatusText.Text = "Не удалось загрузить: " + ex.Message;
            return false;
        }
        finally { _loadingCard = false; }
    }

    private void SetSelectionOnly(bool selectionOnly)
    {
        // Keep the existing selector accessible, but do not allow edits that
        // cannot be saved until the user chooses the actual legal card.
        foreach (var field in new[] { NameBox, CustomerBox, AddressBox, FolderBox, NoteBox,
            CustomerFullBox, SiteFullBox, CustOrgBox, CustPostBox, CustNameBox,
            SecondRoleBox, SecondOrgBox, SecondPostBox, SecondNameBox, WorkTitleBox, WorksStartBox })
            field.IsReadOnly = selectionOnly;
        TagBox.IsEnabled = !selectionOnly;
        LegalDetailsPanel.IsEnabled = !selectionOnly;
        LegalCompanySelector.IsEnabled = !selectionOnly;
        if (selectionOnly)
        {
            AgreementNumberBox.IsReadOnly = AgreementDateBox.IsReadOnly = true;
            AgreementText.Text = "На объекте несколько юридических договоров. Выберите точную карточку.";
        }
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
        if (_saving || _loadingCard || _snapshot is not { } snapshot || snapshot.RequiresLegalSelection || !SaveButton.IsEnabled) return;
        var properties = ReadProperties();
        var print = ReadPrint(snapshot.Print);
        var legal = ReadLegal(print);
        try
        {
            if (legal is not null) SqlServerAgreements.Validate(legal);
            SqlServerWorkspace.ValidateObject(properties);
            SqlServerWorkspace.ValidateSharedPrint(_objectId, print);
        }
        catch (ArgumentException ex) { StatusText.Text = ex.Message; return; }
        _saving = true;
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        StatusText.Text = "Сохранение…";
        long? savedLegalId;
        try
        {
            savedLegalId = await Task.Run(() => SqlServerWorkspace.SaveAgreementCard(_connection, _objectId, properties, print, snapshot.Revision, legal));
        }
        catch (Exception ex)
        {
            if (IsLoaded) StatusText.Text = "Сохранение не подтверждено: " + ex.Message + ". Вернитесь к карточке и откройте реквизиты заново.";
            return;
        }
        finally { _saving = false; }
        // Read the exact saved identity, including a newly inserted legal card.
        if (!IsLoaded) return;
        if (await LoadCard(savedLegalId))
        {
            if (IsLoaded) Saved?.Invoke();
        }
        else if (IsLoaded)
            StatusText.Text = "Реквизиты сохранены, но перечитывание не удалось. Откройте карточку заново.";
    }

    private bool HasUnsavedChanges() => _snapshot is { RequiresLegalSelection: false } snapshot &&
        (ReadProperties() != snapshot.Properties || ReadPrint(snapshot.Print) != snapshot.Print || LegalHasChanges(snapshot));

    private void EditDocument_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingCard || _snapshot is null || sender is not Button { DataContext: SqlServerWorkspace.AgreementCardDocument { CanEdit: true } source }) return;
        if (HasUnsavedChanges())
        {
            StatusText.Text = "Сначала сохраните общие реквизиты или вернитесь к карточке без сохранения.";
            return;
        }
        EditDocumentRequested?.Invoke(source.Document);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingCard) return;
        if (HasUnsavedChanges() &&
            MessageBox.Show(Window.GetWindow(this), "Вернуться без сохранения изменений?", "Реквизиты",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        BackRequested?.Invoke();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { RequiresLegalSelection: false } || _saving || _loadingCard) return;
        var dialog = new OpenFolderDialog { Title = "Папка документов", Multiselect = false };
        if (System.IO.Path.IsPathFullyQualified(FolderBox.Text) && !FolderBox.Text.StartsWith(@"\\"))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) FolderBox.Text = dialog.FolderName;
    }
}
