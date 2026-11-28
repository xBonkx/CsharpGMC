using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

/// <summary>Functional selection of an existing legal file; no write commands.</summary>
internal static class AgreementPdfVersionDialog
{
    internal static SqlServerAgreementFiles.SignedFile? Choose(Window? owner,
        IReadOnlyList<SqlServerAgreementFiles.SignedFile> files)
    {
        var list = new ListBox { ItemsSource = files, DisplayMemberPath = "Caption",
            SelectionMode = SelectionMode.Single, MinHeight = 120, TabIndex = 0 };
        var dialog = new Window
        {
            Title = "Подписанные PDF договора", Width = 620, Height = 350,
            MinWidth = 420, MinHeight = 240, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
        };
        if (owner is not null) dialog.Owner = owner;
        WorkspaceWindowUi.ApplyChrome(dialog);
        var root = new DockPanel { Margin = new Thickness(12) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,10,0,0) };
        DockPanel.SetDock(actions, Dock.Bottom);
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90, TabIndex = 2 };
        var open = new Button { Content = "Открыть", IsEnabled = false, MinWidth = 90, TabIndex = 1, Margin = new Thickness(8,0,0,0) };
        WorkspaceWindowUi.StyleButton(cancel);
        WorkspaceWindowUi.StyleButton(open, primary: true);
        actions.Children.Add(cancel);
        actions.Children.Add(open);
        root.Children.Add(actions);
        root.Children.Add(list);
        dialog.Content = root;
        list.SelectionChanged += (_,_) => open.IsEnabled = list.SelectedItem is SqlServerAgreementFiles.SignedFile;
        open.Click += (_,_) => { if (list.SelectedItem is SqlServerAgreementFiles.SignedFile) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => list.Focus();
        return dialog.ShowDialog() == true ? list.SelectedItem as SqlServerAgreementFiles.SignedFile : null;
    }
}
