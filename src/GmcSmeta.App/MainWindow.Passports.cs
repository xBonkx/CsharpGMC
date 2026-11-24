using System.Windows;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    private void ObjectPassports_Click(object sender, RoutedEventArgs e) =>
        OpenObjectTool("Паспорта вентиляции", (connection, obj) =>
            new PassportsWindow(connection, obj.Id, obj.Name, obj.Address));

    private void ObjectPto_Click(object sender, RoutedEventArgs e)
    {
        if (PtoUserSettings.Enabled)
            OpenObjectTool("Исполнительная документация", (connection, obj) => new PtoWindow(connection, obj.Id));
    }

    private void ObjectLetters_Click(object sender, RoutedEventArgs e) =>
        OpenObjectTool("Письма объекта", (connection, obj) => new LettersWindow(
            connection, obj, _docs.Where(doc => doc.ObjectId == obj.Id).Select(doc => (long)doc.Id).ToHashSet()));

    private void ObjectAccess_Click(object sender, RoutedEventArgs e) =>
        OpenObjectTool("Допуск", (connection, obj) => new PeopleWindow(connection, obj.Id));

    private void ObjectOrders_Click(object sender, RoutedEventArgs e) =>
        OpenObjectTool("Приказы", (connection, obj) =>
            new OrdersWindow(new SqlServerPeopleAccess(connection), obj.Id));

    private void OpenObjectTool(string title, Func<string, ObjectRow, Window> create) =>
        OpenObjectTool(_selectedObject, title, create);

    private void OpenObjectTool(ObjectRow? target, string title, Func<string, ObjectRow, Window> create)
    {
        if (!SqlWritesEnabled || _sqlConnectionString is not { } connection || target is null ||
            _objects.FirstOrDefault(item => item.Id == target.Id) is not { } obj)
            return;
        RunNavigationAction(title, () =>
        {
            var window = create(connection, obj);
            window.Owner = this;
            window.ShowDialog();
        });
    }
}
