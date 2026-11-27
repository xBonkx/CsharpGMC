using System.Windows;

namespace GmcSmeta.App;

/// <summary>Compatibility host for the object-specific access-list dialog and external callers.</summary>
public class PeopleWindow : Window
{
    private readonly PeopleView _view;

    public PeopleWindow(string connectionString, long? objectId = null)
    {
        _view = new PeopleView(connectionString, objectId);
        Title = _view.Title;
        Width = 1200;
        Height = 760;
        MinWidth = 840;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = _view;
        Loaded += (_, _) => WindowState = WindowState.Maximized;
        _view.CloseRequested += Close;
        Closing += (_, e) => { if (!_view.TryLeave()) e.Cancel = true; };
        Closed += (_, _) => _view.CloseView();
    }
}
