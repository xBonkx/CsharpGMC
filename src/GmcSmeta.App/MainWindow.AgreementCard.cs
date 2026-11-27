using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private void OpenAgreementCard(ObjectRow target)
    {
        if (_sqlConnectionString is not { } connection || !SqlWritesEnabled || target.Archived) return;
        ActivateDocumentsWorkspace();
        EndPageRename();
        _selectedObject = target;
        _openDocumentId = null;
        var page = new AgreementCardView(connection, target.Id);
        page.BackRequested += () =>
        {
            if (ReferenceEquals(PageHost.Content, page)) ReloadAndSelect(target.Id);
        };
        page.Saved += () =>
        {
            if (ReferenceEquals(PageHost.Content, page)) ReloadAndSelect(target.Id);
        };
        ShowPage(page);
        StatusText.Text = target.Name + " · реквизиты договора";
    }
}
