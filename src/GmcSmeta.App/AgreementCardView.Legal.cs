using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class AgreementCardView
{
    private bool _loadingLegal;
    private SqlServerAgreements.Values? _legalBaseline;

    private void InitializeLegal(SqlServerWorkspace.AgreementCardSnapshot snapshot)
    {
        _loadingLegal = true;
        try
        {
            LegalSelectorPanel.Visibility = LegalDetailsPanel.Visibility = LegalCompanySelector.Visibility = snapshot.LegalEnabled ? Visibility.Visible : Visibility.Collapsed;
            CompanyBox.Visibility = snapshot.LegalEnabled ? Visibility.Collapsed : Visibility.Visible;
            AgreementNumberBox.IsReadOnly = AgreementDateBox.IsReadOnly = !snapshot.LegalEnabled;
            LegalSelector.ItemsSource = snapshot.LegalCards;
            LegalSelector.SelectedValue = snapshot.SelectedLegal?.Id;
            LegalCompanySelector.ItemsSource = snapshot.LegalCompanies;
            var legal = snapshot.SelectedLegal;
            if (snapshot.LegalEnabled)
            {
                AgreementNumberBox.Text = legal?.Number ?? "";
                AgreementDateBox.Text = legal?.Date ?? "";
                LegalCompanySelector.SelectedValue = legal?.CompanyId;
                LegalPdfBox.Text = snapshot.SignedLegalPdf;
                LegalSubjectBox.Text = legal?.Subject ?? "";
                LegalCustomerAddressBox.Text = legal?.CustomerAddress ?? "";
                LegalContractorFullBox.Text = legal?.ContractorFull ?? "";
                LegalContractorPostBox.Text = legal?.ContractorPosition ?? "";
                LegalContractorNameBox.Text = legal?.ContractorName ?? "";
                LegalWorksEndBox.Text = legal?.WorksEnd ?? "";
                AgreementText.Text = "Реквизиты принадлежат выбранному договору. Его можно сохранить до создания первого приложения.";
                _legalBaseline = ReadLegal(snapshot.Print);
            }
            else _legalBaseline = null;
        }
        finally { _loadingLegal = false; }
    }

    private SqlServerAgreements.Values? ReadLegal(SqlServerKsRequisites.Values print) =>
        _snapshot is { LegalEnabled: true } snapshot ? new(snapshot.SelectedLegal?.Id ?? 0, _objectId,
            AgreementNumberBox.Text, AgreementDateBox.Text, LegalCompanySelector.SelectedValue is long id ? id : null,
            LegalSubjectBox.Text, LegalWorksEndBox.Text, LegalCustomerAddressBox.Text, LegalContractorFullBox.Text,
            LegalContractorPostBox.Text, LegalContractorNameBox.Text, print) : null;

    private bool LegalHasChanges(SqlServerWorkspace.AgreementCardSnapshot snapshot) =>
        snapshot.LegalEnabled && ReadLegal(ReadPrint(snapshot.Print)) != _legalBaseline;

    private async void LegalSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLegal || _saving || _loadingCard || LegalSelector.SelectedValue is not long id || id == _snapshot?.SelectedLegal?.Id) return;
        if (HasUnsavedChanges())
        {
            StatusText.Text = "Сначала сохраните текущие реквизиты.";
            _loadingLegal = true;
            LegalSelector.SelectedValue = _snapshot?.SelectedLegal?.Id;
            _loadingLegal = false;
            return;
        }
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        await LoadCard(id);
    }

    private async void NewLegal_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingCard || _snapshot is null) return;
        if (HasUnsavedChanges()) { StatusText.Text = "Сначала сохраните текущие реквизиты."; return; }
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        await LoadCard(0);
    }

    private async void LegalPdf_Add(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingCard || _snapshot is not { SelectedLegal: { } legal } snapshot)
        { StatusText.Text = "Сначала сохраните договор."; return; }
        if (HasUnsavedChanges()) { StatusText.Text = "Сначала сохраните реквизиты и папку документов."; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf", Title = "Подписанный договор" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (!IsLoaded || _saving || _loadingCard || !ReferenceEquals(snapshot, _snapshot) || HasUnsavedChanges()) return;
        var source = dialog.FileName;
        _saving = true; Fields.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            await Task.Run(() => SqlServerAgreementFiles.AddSignedPdf(_connection, legal.Id, source, snapshot.Revision));
            if (await LoadCard(legal.Id))
                StatusText.Text = "Добавлен новый PDF. Ранее добавленные файлы сохранены.";
            else if (IsLoaded)
                StatusText.Text = "PDF зарегистрирован, но перечитывание карточки не удалось. Откройте её заново.";
        }
        catch (Exception error)
        {
            _snapshot = null;
            if (IsLoaded) StatusText.Text = error.Message + " Откройте карточку заново перед повтором.";
        }
        finally { _saving = false; }
    }

    private async void LegalPdf_Open(object sender, RoutedEventArgs e)
    {
        if (_saving || _loadingCard || _snapshot is not { SelectedLegal: { } legal } snapshot ||
            string.IsNullOrWhiteSpace(snapshot.SignedLegalPdf)) return;
        _saving = true;
        Fields.IsEnabled = SaveButton.IsEnabled = false;
        try
        {
            var path = await Task.Run(() => SqlServerAgreementFiles.VerifyLatest(_connection,
                _objectId, legal.Id, snapshot.SignedLegalPdf, snapshot.Revision));
            if (IsLoaded && ReferenceEquals(snapshot, _snapshot))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { if (IsLoaded) StatusText.Text = error.Message; }
        finally
        {
            _saving = false;
            if (IsLoaded && ReferenceEquals(snapshot, _snapshot)) Fields.IsEnabled = SaveButton.IsEnabled = true;
        }
    }

    private void LegalCompany_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingLegal || LegalCompanySelector.SelectedItem is not SqlServerAgreements.Company company) return;
        LegalContractorFullBox.Text = string.IsNullOrWhiteSpace(company.FullName) ? company.Name : company.FullName;
        LegalContractorPostBox.Text = company.Position;
        LegalContractorNameBox.Text = company.Signer;
    }
}
