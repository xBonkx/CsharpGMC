using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Что вернул диалог документа. CopyFrom — только для «Новое КП» (копия).</summary>
public sealed record ContractDialogResult(string Kind, string Number, string Date, string Name,
    long? CompanyId, long? BaseContractId, decimal? VatRate, string Note, string Form, long? CopyFrom)
{
    public long? AgreementId { get; init; }
    public string AnnexNumber { get; init; } = "";
}

/// <summary>
/// Один диалог на два случая Python: «Свойства документа» (ContractDialog) и
/// «Новое КП / допсоглашение» (NewContractDialog). Поля, правила и значения — как в Python;
/// оформление простое, его доводит дизайн.
/// </summary>
public partial class ContractDialog : Window
{
    private sealed record KindItem(string Key, string Title)
    {
        public override string ToString() => Title;
    }

    private sealed record IdItem(long? Id, string Title)
    {
        public override string ToString() => Title;
    }

    private sealed record TagItem(string Key, string Title)
    {
        public override string ToString() => Title;
    }

    private static readonly KindItem[] Kinds =
    {
        new("kp", "КП"), new("main", "Приложение"), new("dop", "Допсоглашение"),
    };

    private readonly bool _isNew;
    private readonly AgreementPresentation.Requisites? _agreement;
    private readonly Dictionary<long, decimal> _vatByCompany = new();
    private IReadOnlyDictionary<long, long?>? _baseCompanies;

    public ContractDialogResult? Result { get; private set; }
    public bool ImportExcelRequested { get; private set; }

    /// <summary>Свойства существующего документа (ContractDialog в Python).</summary>
    public ContractDialog(string objectName, ContractInfo info, IReadOnlyList<CompanyInfo> companies,
                          IReadOnlyList<ObjectTag> tags, IReadOnlyList<(long Id, string Title)> mainContracts,
                          bool makeContract, AgreementPresentation.Requisites? agreement = null)
    {
        InitializeComponent();
        _isNew = false;
        _preserveLegacyLegalMetadata=info.Kind=="main" && !makeContract;
        _agreement = makeContract ? agreement : null;
        ContentPanel.Visibility = Visibility.Collapsed;
        Title = makeContract ? "Оформить как приложение" : "Свойства документа";
        CaptionText.Text = makeContract ? "Оформить как приложение" : "Свойства документа";
        OkButton.Content = "Сохранить";
        ObjectText.Text = objectName;
        FillKinds(makeContract ? "main" : info.Kind);
        FillCompanies(companies, info.CompanyId);
        FillTags(tags, info.Form);
        FillBase(mainContracts.Where(x => x.Id != info.Id), info.BaseContractId);
        NumberBox.Text = info.Number;
        DateBox.Text = info.Date;
        NameBox.Text = info.Name;
        NoteBox.Text = info.Note;
        VatBox.Text = info.VatRate.ToString("0.###", CultureInfo.CurrentCulture);
        ShowVatHint();                 // подсказка по организации, но НДС документа не меняем
        SyncKind();
    }

    /// <summary>Новое КП, договор, копия КП или допсоглашение (NewContractDialog в Python).</summary>
    public ContractDialog(string objectName, IReadOnlyList<(long Id, string Title, string Kind)> contracts,
                          IReadOnlyList<CompanyInfo> companies, IReadOnlyList<ObjectTag> tags,
                          string kind = "kp", long? baseId = null, long? copyFromId = null,
                          long? defaultCompany = null, bool allowCopy = true, bool allowExcelImport = false,
                          AgreementPresentation.Requisites? agreement = null,
                          IReadOnlyDictionary<long, long?>? baseCompanies = null)
    {
        InitializeComponent();
        _isNew = true;
        _agreement = agreement;
        ImportExcelButton.Visibility = allowExcelImport ? Visibility.Visible : Visibility.Collapsed;
        Title = kind == "dop" ? "Новое допсоглашение" : kind == "main" ? "Новое приложение" : "Добавить документ";
        CaptionText.Text = Title;
        OkButton.Content = "Создать";
        ObjectText.Text = objectName;
        FillKinds(kind);
        FillCompanies(companies, defaultCompany);
        FillTags(tags, "");
        FillBase(contracts.Where(x => (x.Kind is "main" or "kp") && (baseCompanies is null || baseCompanies.ContainsKey(x.Id)))
            .Select(x => (x.Id, x.Title)), baseId);
        _baseCompanies = baseCompanies;
        if (baseCompanies is not null)
        {
            BaseCombo.ToolTip = "При выборе основания нового ДС подставляется его организация; проверьте реквизиты перед созданием.";
            BaseCombo.SelectionChanged += (_, _) => ApplySelectedBaseCompany();
        }

        SourceCombo.ItemsSource = contracts.Select(c => new IdItem(c.Id, c.Title)).ToList();
        var source = (IReadOnlyList<IdItem>)SourceCombo.ItemsSource;
        SourceCombo.SelectedItem = source.FirstOrDefault(x => x.Id == copyFromId) ?? source.FirstOrDefault();
        CopyRadio.IsEnabled = allowCopy && contracts.Count > 0;
        CopyRadio.IsChecked = allowCopy && copyFromId is not null && contracts.Count > 0;
        EmptyRadio.IsChecked = CopyRadio.IsChecked != true;
        SourceCombo.IsEnabled = CopyRadio.IsChecked == true;

        DateBox.Text = DateTime.Today.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture);

        // В NewContractDialog Python нет полей НДС, тега и примечания: НДС берётся по организации,
        // тег — из объекта (builder.create_contract), примечание пустое.
        VatLabel.Visibility = VatBox.Visibility = Visibility.Collapsed;
        TagLabel.Visibility = TagCombo.Visibility = TagNote.Visibility = Visibility.Collapsed;
        NoteLabel.Visibility = NoteBox.Visibility = Visibility.Collapsed;

        ApplyCompanyVat();
        SyncKind();
        ApplySelectedBaseCompany();
        if (SelectedKind() == "main" && copyFromId is null) ApplyAgreementDefaults();
    }

    private void FillKinds(string current)
    {
        KindCombo.ItemsSource = Kinds;
        KindCombo.SelectedItem = Kinds.FirstOrDefault(k => k.Key == current) ?? Kinds[0];
    }

    private void FillCompanies(IReadOnlyList<CompanyInfo> companies, long? current)
    {
        var items = new List<IdItem> { new(null, "— не указана —") };
        items.AddRange(companies.Select(c => new IdItem(c.Id, c.Name)));
        foreach (var c in companies) _vatByCompany[c.Id] = c.VatRate;
        CompanyCombo.ItemsSource = items;

        var selected = current is not null ? items.FirstOrDefault(x => x.Id == current) : null;
        if (selected is null && current is null)
        {
            // Как _company_combo в Python: по умолчанию наша организация «ГМЦ».
            selected = items.FirstOrDefault(x => x.Title.Contains("ГМЦ", StringComparison.OrdinalIgnoreCase))
                       ?? (items.Count > 1 ? items[1] : items[0]);
        }
        CompanyCombo.SelectedItem = selected ?? items[0];
    }

    private void FillTags(IReadOnlyList<ObjectTag> tags, string current)
    {
        var items = tags.Select(t => new TagItem(t.Key, t.Caption)).ToList();
        TagCombo.ItemsSource = items;
        TagCombo.SelectedItem = items.FirstOrDefault(t => t.Key == (current ?? "")) ?? items[0];
    }

    private void FillBase(IEnumerable<(long Id, string Title)> contracts, long? current)
    {
        var items = new List<IdItem> { new(null, "—") };
        items.AddRange(contracts.Select(c => new IdItem(c.Id, c.Title)));
        BaseCombo.ItemsSource = items;
        BaseCombo.SelectedItem = items.FirstOrDefault(x => x.Id == current) ?? items[0];
    }

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SyncKind();
        ApplySelectedBaseCompany();
    }

    private void ApplySelectedBaseCompany()
    {
        if (SelectedKind() != "dop" || BaseCombo is null || CompanyCombo is null ||
            _baseCompanies is null || SelectedId(BaseCombo) is not long id ||
            !_baseCompanies.TryGetValue(id, out var companyId)) return;
        if (CompanyCombo.ItemsSource is IEnumerable<IdItem> items)
            CompanyCombo.SelectedItem = items.FirstOrDefault(c => c.Id == companyId) ?? items.First();
    }

    private void SyncKind()
    {
        if (BaseCombo is null) return;
        var kind = SelectedKind();
        BaseCombo.IsEnabled = kind == "dop";
        if (NumberLabel is not null) NumberLabel.Text = kind == "main" ? "Номер договора" : kind == "dop" ? "Номер ДС" : "Номер КП";
        if (DateLabel is not null) DateLabel.Text = kind == "main" ? "Дата договора" : kind == "dop" ? "Дата ДС" : "Дата КП";
        if (AgreementDefaultsButton is not null)
            AgreementDefaultsButton.Visibility = kind == "main" && _agreement is not null
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AgreementDefaults_Click(object sender, RoutedEventArgs e) => ApplyAgreementDefaults();

    private void ApplyAgreementDefaults()
    {
        if (SelectedKind() != "main" || _agreement is not { } agreement) return;
        if (CompanyCombo.ItemsSource is not IEnumerable<IdItem> companies ||
            companies.FirstOrDefault(c => c.Id == agreement.CompanyId) is not { } company) return;
        NumberBox.Text = agreement.Number;
        DateBox.Text = agreement.Date;
        // Changing the selected company already triggers Company_SelectionChanged.
        // Preserve the existing document VAT when conversion keeps the same company.
        CompanyCombo.SelectedItem = company;
        if (_isNew) ApplyCompanyVat();
    }

    private void Content_Changed(object sender, RoutedEventArgs e)
    {
        if (SourceCombo is null) return;
        SourceCombo.IsEnabled = CopyRadio.IsChecked == true;
    }

    private void Company_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyCompanyVat();

    /// <summary>Как Python: при выборе организации НДС ставится по ней же.</summary>
    private void ApplyCompanyVat()
    {
        if (CompanyCombo?.SelectedItem is not IdItem { Id: long id }) return;
        if (!_vatByCompany.TryGetValue(id, out var vat)) return;
        VatBox.Text = vat.ToString("0.###", CultureInfo.CurrentCulture);
        VatHint.Text = $"НДС {vat.ToString("0.###", CultureInfo.CurrentCulture)}% — по организации";
    }

    /// <summary>Только подсказка: НДС уже может быть своим, как в ContractDialog Python.</summary>
    private void ShowVatHint()
    {
        if (CompanyCombo?.SelectedItem is not IdItem { Id: long id })
        {
            VatHint.Text = "";
            return;
        }
        VatHint.Text = _vatByCompany.TryGetValue(id, out var vat)
            ? $"НДС организации: {vat.ToString("0.###", CultureInfo.CurrentCulture)}%"
            : "";
    }

    private void ImportExcel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isNew || ImportExcelButton.Visibility != Visibility.Visible) return;
        // A separate outcome: no empty document, no date validation, no copy.
        ImportExcelRequested = true;
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedKind() == "main" && !_preserveLegacyLegalMetadata &&
            LegalAgreementPanel.Visibility == Visibility.Visible &&
            LegalAgreementCombo.SelectedValue is not long)
        {
            MessageBox.Show(this, "Выберите юридический договор для приложения.",
                "Реквизиты", MessageBoxButton.OK, MessageBoxImage.Warning);
            LegalAgreementCombo.Focus();
            return;
        }
        var date = DateBox.Text.Trim();
        if (!Documents.IsValidDocumentDate(date))
        {
            MessageBox.Show(this, "Введите существующую дату, например 23.04.2026.",
                "Дата", MessageBoxButton.OK, MessageBoxImage.Warning);
            DateBox.Focus();
            DateBox.SelectAll();
            return;
        }

        var kind = SelectedKind();
        var copyFrom = _isNew && CopyRadio.IsChecked == true ? SelectedId(SourceCombo) : null;
        Result = new ContractDialogResult(kind, _preserveLegacyLegalMetadata && kind=="main" && _legacyLegalMetadata is { } original ? original.Number : NumberBox.Text.Trim(),
            _preserveLegacyLegalMetadata && kind=="main" && _legacyLegalMetadata is { } originalDate ? originalDate.Date : date, NameBox.Text.Trim(),
            _preserveLegacyLegalMetadata && kind=="main" && _legacyLegalMetadata is { } originalCompany ? originalCompany.Company : SelectedId(CompanyCombo), kind == "dop" ? SelectedId(BaseCombo) : null,
            VatValue(), NoteBox.Text, (TagCombo.SelectedItem as TagItem)?.Key ?? "", copyFrom) { AgreementId = kind == "main" && LegalAgreementCombo.SelectedValue is long agreementId ? agreementId : null, AnnexNumber = kind == "main" ? LegalAnnexNumberBox.Text.Trim() : "" };
        DialogResult = true;
    }

    private string SelectedKind() => (KindCombo.SelectedItem as KindItem)?.Key ?? "kp";

    private static long? SelectedId(ComboBox combo) =>
        combo.SelectedItem is IdItem item ? item.Id : null;

    private decimal? VatValue()
    {
        var text = VatBox.Text.Trim().Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value : null;
    }
}
