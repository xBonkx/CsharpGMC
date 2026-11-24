using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>
/// Decoupled integration hook for navigation owner (backlog 13). No MainWindow files modified.
/// Example: PeopleAccessWindows.OpenPeople(owner, connectionString) or
/// PeopleAccessWindows.OpenPeople(owner, connectionString, objectId).
/// </summary>
public static class PeopleAccessWindows
{
    public static bool? OpenPeople(Window? owner, string connectionString, long? objectId = null)
    {
        Window dialog = objectId is long id
            ? new AccessListWindow(connectionString, id)
            : new PeopleWindow(connectionString);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog();
    }

    public static bool? OpenOrders(Window? owner, string connectionString, long objectId)
    {
        var database = new SqlServerPeopleAccess(connectionString);
        var dialog = new OrdersWindow(database, objectId);
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog();
    }
}

/// <summary>
/// Dedicated object access-list entry point; reuses the same dual-mode WPF window as Python
/// PeopleDialog(object_id=...), with separate object history and export.
/// </summary>
public sealed class AccessListWindow : PeopleWindow
{
    public AccessListWindow(string connectionString, long objectId) : base(connectionString, objectId) { }
}