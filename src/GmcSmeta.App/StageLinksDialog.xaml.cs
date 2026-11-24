using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class StageLinksDialog : Window
{
    public StageLinksDialog(IReadOnlyList<SqlServerStages.StageDocumentLink> links)
    {
        InitializeComponent();
        LinksGrid.ItemsSource = links;
        StatusText.Text = $"Документов: {links.Count}. Подписанные КС и дочерние ДС защищены от удаления.";
    }
}
