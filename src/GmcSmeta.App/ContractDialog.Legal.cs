using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class ContractDialog
{
    private bool _preserveLegacyLegalMetadata;
    private (string Number,string Date,long? Company)? _legacyLegalMetadata;
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
            NumberBox.Text = selected.Number;
            DateBox.Text = selected.Date;
            CompanyCombo.SelectedValue = selected.CompanyId;
            NumberBox.IsReadOnly = DateBox.IsReadOnly = true;
            CompanyCombo.IsEnabled = false;
        }
        LegalAgreementCombo.SelectionChanged += (_, _) => Apply();
        KindCombo.SelectionChanged += (_, _) =>
        {
            LegalAgreementPanel.Visibility = SelectedKind() == "main" ? Visibility.Visible : Visibility.Collapsed;
            if(SelectedKind()!="main") { NumberBox.IsReadOnly=DateBox.IsReadOnly=false;CompanyCombo.IsEnabled=true; }
            else Apply();
        };
        LegalAgreementPanel.Visibility = SelectedKind() == "main" ? Visibility.Visible : Visibility.Collapsed;
        if(selectedId.HasValue) LegalAgreementCombo.SelectedValue=selectedId.Value;
        else if (cards.Count == 1) LegalAgreementCombo.SelectedIndex = 0;
    }
}
