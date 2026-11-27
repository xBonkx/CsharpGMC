using System.Windows;
using System.Windows.Controls;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private PeopleView? _peopleWorkspace;
    private bool _restoringWorkspaceTab;

    private void OpenPeopleWorkspace()
    {
        if (_sqlConnectionString is not { } connection || _startupClosed) return;
        if (_peopleWorkspace is null)
        {
            var view = new PeopleView(connection);
            view.CloseRequested += ClosePeopleWorkspace;
            _peopleWorkspace = view;
            PeopleWorkspaceHost.Content = view;
        }
        AuxWorkspaceTabs.Visibility = Visibility.Visible;
        AuxWorkspaceTabs.SelectedItem = PeopleWorkspaceTab;
    }

    private void ActivateDocumentsWorkspace()
    {
        if (_peopleWorkspace is not null)
            AuxWorkspaceTabs.SelectedItem = DocumentsWorkspaceTab;
    }

    private void AuxWorkspaceTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, AuxWorkspaceTabs) || _restoringWorkspaceTab ||
            DocumentsWorkspaceSurface is null || PeopleWorkspaceHost is null) return;
        var showPeople = ReferenceEquals(AuxWorkspaceTabs.SelectedItem, PeopleWorkspaceTab) &&
            _peopleWorkspace is not null;
        if (!showPeople && PeopleWorkspaceHost.Visibility == Visibility.Visible &&
            _peopleWorkspace is { } view && !view.TryLeave())
        {
            _restoringWorkspaceTab = true;
            try { AuxWorkspaceTabs.SelectedItem = PeopleWorkspaceTab; }
            finally { _restoringWorkspaceTab = false; }
            return;
        }
        DocumentsWorkspaceSurface.Visibility = showPeople ? Visibility.Collapsed : Visibility.Visible;
        PeopleWorkspaceHost.Visibility = showPeople ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClosePeopleWorkspace()
    {
        if (_peopleWorkspace is not { } view || !view.TryLeave()) return;
        view.CloseRequested -= ClosePeopleWorkspace;
        view.CloseView();
        _peopleWorkspace = null;
        AuxWorkspaceTabs.SelectedItem = DocumentsWorkspaceTab;
        PeopleWorkspaceHost.Visibility = Visibility.Collapsed;
        PeopleWorkspaceHost.Content = null;
        DocumentsWorkspaceSurface.Visibility = Visibility.Visible;
        AuxWorkspaceTabs.Visibility = Visibility.Collapsed;
    }
}
