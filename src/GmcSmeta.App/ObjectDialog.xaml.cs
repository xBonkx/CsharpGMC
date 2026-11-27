using System.Windows;
using GmcSmeta.Core.Domain;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class ObjectDialog : Window
{
    public ObjectProperties? Values { get; private set; }
    public ObjectDialog(IReadOnlyList<ObjectTag> tags, ObjectProperties? properties = null,
        AgreementPresentation.Requisites? agreement = null)
    {
        InitializeComponent();
        Heading.Text = properties is null ? "Новый договор" : "Свойства карточки";
        Title = Heading.Text;
        AgreementInfoText.Text = agreement is not null ? agreement.Heading + " · реквизиты из приложений (только просмотр)" :
            "Номер, дата договора и подрядчик пока задаются в приложениях; общие реквизиты карточки — ниже.";
        var value = properties ?? new ObjectProperties("");
        NameInput.Text = value.Name; CustomerInput.Text = value.Customer; AddressInput.Text = value.Address;
        FolderInput.Text = value.Folder; NoteInput.Text = value.Note;
        TagInput.ItemsSource = tags; TagInput.SelectedValue = value.Form;
        if (TagInput.SelectedIndex < 0) TagInput.SelectedIndex = 0;
        Loaded += (_, _) => NameInput.Focus();
    }
    public bool TryReadValues()
    {
        try
        {
            Values = new ObjectProperties(NameInput.Text, CustomerInput.Text, AddressInput.Text,
                FolderInput.Text, NoteInput.Text, TagInput.SelectedValue as string ?? "").Normalize();
            ValidationMessage.Text = ""; return true;
        }
        catch (ArgumentException ex)
        {
            Values = null; ValidationMessage.Text = ex.Message; NameInput.Focus(); return false;
        }
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (TryReadValues()) DialogResult = true;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка объекта", Multiselect = false };
        // Merely editing properties must not probe a stored network path.
        if (System.IO.Path.IsPathFullyQualified(FolderInput.Text) && !FolderInput.Text.StartsWith(@"\\"))
            dialog.InitialDirectory = FolderInput.Text;
        if (dialog.ShowDialog(this) == true) FolderInput.Text = dialog.FolderName;
    }
}
