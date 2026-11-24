using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Что вернул диалог документа. CopyFrom — только для «Новое КП» (копия).</summary>
public sealed record ContractDialogResult(string Kind, string Number, string Date, string Name,
    long? CompanyId, long? BaseContractId, decimal? VatRate, string Note, string Form, long? CopyFrom);

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
        new("kp", "КП"), new("main", "Договор"), new("dop", "Допсоглашение"),
    };

    private readonly bool _isNew;
    private readonly Dictionary<long, decimal> _vatByCompany = new();

    public ContractDialogResult? Result { get; private set; }

    /// <summary>Свойства существующего документа (ContractDialog в Python).</summary>
    public ContractDialog(string objectName, ContractInfo info, IReadOnlyList<CompanyInfo> companies,
                          IReadOnlyList<ObjectTag> tags, IReadOnlyList<(long Id, string Title)> mainContracts,
                          bool makeContract)
    {
        InitializeComponent();
        _isNew = false;
        ContentPanel.Visibility = Visibility.Collapsed;
        Title = makeContract ? "Сделать договором" : "Свойства документа";
        CaptionText.Text = makeContract ? "Сделать договором" : "Свойства документа";
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
                          long? defaultCompany = null, bool allowCopy = true)
    {
        InitializeComponent();
        _isNew = true;
        Title = kind == "dop" ? "Новое допсоглашение" : "Новое КП";
        CaptionText.Text = "Новое КП / допсоглашение";
        OkButton.Content = "Создать";
        ObjectText.Text = objectName;
        FillKinds(kind);
        FillCompanies(companies, defaultCompany);
        FillTags(tags, "");
        FillBase(contracts.Where(x => x.Kind is "main" or "kp").Select(x => (x.Id, x.Title)), baseId);

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

    private void Kind_SelectionChanged(object sender, SelectionChangedEventArgs e) => SyncKind();

    private void SyncKind()
    {
        if (BaseCombo is null) return;
        BaseCombo.IsEnabled = SelectedKind() == "dop";
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

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
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
        Result = new ContractDialogResult(kind, NumberBox.Text.Trim(), date, NameBox.Text.Trim(),
            SelectedId(CompanyCombo), kind == "dop" ? SelectedId(BaseCombo) : null,
            VatValue(), NoteBox.Text, (TagCombo.SelectedItem as TagItem)?.Key ?? "", copyFrom);
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
