using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ContractDialog
{
    private bool _preserveLegacyLegalMetadata;
    private (string Number,string Date,long? Company)? _legacyLegalMetadata;
    private (string Number,string Date,long? Company,string Vat)? _independentMetadata;
    private bool _legalFieldsApplied;
    internal void ConfigureAgreements(IReadOnlyList<SqlServerAgreements.Values> cards,long? selectedId=null,string annexNumber="")
    {
        if (cards.Count == 0) return;
        _legacyLegalMetadata = (NumberBox.Text,DateBox.Text,SelectedId(CompanyCombo));
        CompanyCombo.SelectedValuePath="Id";
        LegalAnnexNumberBox.Text = annexNumber;
        LegalAgreementPanel.Visibility = Visibility.Visible;
        LegalAgreementCombo.ItemsSource = cards;
        void Apply()
        {
            if (SelectedKind() != "main" || LegalAgreementCombo.SelectedItem is not SqlServerAgreements.Values selected) return;
            if (!_legalFieldsApplied)
                _independentMetadata = (NumberBox.Text, DateBox.Text, SelectedId(CompanyCombo), VatBox.Text);
            _legalFieldsApplied = true;
            NumberBox.Text = selected.Number;
            DateBox.Text = selected.Date;
            var priorVat = VatBox.Text;
            CompanyCombo.SelectedValue = selected.CompanyId;
            if (_preserveLegacyLegalMetadata) VatBox.Text = priorVat;
            NumberBox.IsReadOnly = DateBox.IsReadOnly = true;
            CompanyCombo.IsEnabled = false;
        }
        LegalAgreementCombo.SelectionChanged += (_, _) => Apply();
        KindCombo.SelectionChanged += (_, _) =>
        {
            LegalAgreementPanel.Visibility = SelectedKind() == "main" ? Visibility.Visible : Visibility.Collapsed;
            if (SelectedKind() != "main")
            {
                NumberBox.IsReadOnly = DateBox.IsReadOnly = false;
                CompanyCombo.IsEnabled = true;
                if (_legalFieldsApplied && _independentMetadata is { } previous)
                {
                    NumberBox.Text = previous.Number;
                    DateBox.Text = previous.Date;
                    // DS must keep the organization of its selected base.
                    if (SelectedKind() == "dop") ApplySelectedBaseCompany();
                    else
                    {
                        CompanyCombo.SelectedValue = previous.Company;
                        VatBox.Text = previous.Vat;
                    }
                }
                _legalFieldsApplied = false;
            }
            else Apply();
        };
        LegalAgreementPanel.Visibility = SelectedKind() == "main" ? Visibility.Visible : Visibility.Collapsed;
        if (selectedId.HasValue) LegalAgreementCombo.SelectedValue = selectedId.Value;
        else LegalAgreementCombo.SelectedIndex = -1;
    }
}
