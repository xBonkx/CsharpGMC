using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace GmcSmeta.App;

/// <summary>One consistent double-click entry point; WPF still owns edit/commit/validation.</summary>
internal static class GridEditInteraction
{
    internal static void Install() =>
        EventManager.RegisterClassHandler(typeof(DataGrid), Mouse.PreviewMouseDownEvent,
            new MouseButtonEventHandler(BeginOnDoubleClick));

    private static void BeginOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || grid.IsReadOnly || !grid.IsEnabled ||
            e.ChangedButton != MouseButton.Left || e.ClickCount != 2 || Keyboard.Modifiers != ModifierKeys.None)
            return;
        // Don't intercept header resize, scrolling, nested controls or interactive cell content.
        DataGridCell? cell = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null; node = Parent(node))
        {
            if (node is ButtonBase or TextBoxBase or Selector or Thumb or PasswordBox or ScrollBar)
                return;
            if (node is DataGridCell hit) { cell = hit; break; }
            if (node is DataGrid) return;
        }
        if (cell is null || cell.IsEditing || cell.IsReadOnly || !cell.IsEnabled ||
            Ancestor<DataGrid>(cell) != grid || Ancestor<DataGridRow>(cell) is not { } row ||
            cell.Column is not { IsReadOnly: false } column) return;
        // Let the first click select the cell normally; the second explicitly enters edit.
        // Focus may commit an earlier edit, so re-check identity after it.
        if (!cell.IsKeyboardFocusWithin && !cell.Focus()) return;
        if (!ReferenceEquals(row.Item, grid.CurrentCell.Item) || grid.CurrentCell.Column != column ||
            !ReferenceEquals(row.Item, cell.DataContext) || cell.IsEditing) return;
        // BeginEdit raises BeginningEdit and honors read-only/lock/revision guards.
        if (grid.BeginEdit(e)) e.Handled = true;
    }

    internal static void FocusTextEditor(DataGrid grid, DataGridPreparingCellForEditEventArgs e)
    {
        var row = e.Row;
        var item = row.Item;
        var column = e.Column;
        var element = e.EditingElement;
        // Template columns can enter edit without focusing their inner TextBox.
        // Prefer synchronous focus so the very next Ctrl+V or keystroke is
        // delivered to the editor. Retry once after template realization.
        bool FocusCurrentEditor()
        {
            if (!grid.IsLoaded || !grid.IsKeyboardFocusWithin ||
                !ReferenceEquals(grid.CurrentCell.Item, item) || grid.CurrentCell.Column != column ||
                !ReferenceEquals(row.Item, item) || Ancestor<DataGridCell>(element) is not { IsEditing: true } cell ||
                cell.Column != column || Ancestor<DataGrid>(cell) != grid)
                return false;
            if (element is ContentPresenter presenter) presenter.ApplyTemplate();
            if (FindTextEditor(element) is not { IsLoaded: true } editor)
                return false;
            if (!editor.IsKeyboardFocusWithin && !editor.Focus()) return false;
            // Do not replace the first character or the '=' formula trigger.
            if (e.EditingEventArgs is not TextCompositionEventArgs) editor.SelectAll();
            return true;
        }

        if (FocusCurrentEditor()) return;
        grid.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            FocusCurrentEditor();
        }));
    }

    /// <summary>
    /// An active template editor owns paste even in the small interval before
    /// keyboard focus is transferred from the DataGridCell to its TextBox.
    /// Never reinterpret this Ctrl+V as a structural row insert.
    /// </summary>
    internal static bool TryPasteInEditingCell(DataGrid grid)
    {
        if (!grid.IsKeyboardFocusWithin || grid.CurrentCell is not { IsValid: true, Column: { } column } current ||
            column.GetCellContent(current.Item) is not { } element ||
            Ancestor<DataGridCell>(element) is not { IsEditing: true } cell ||
            cell.Column != column || Ancestor<DataGrid>(cell) != grid ||
            !ReferenceEquals(cell.DataContext, current.Item)) return false;

        if (element is ContentPresenter presenter) presenter.ApplyTemplate();
        if (FindTextEditor(element) is { } editor && editor.Focus() &&
            ApplicationCommands.Paste.CanExecute(null, editor))
            ApplicationCommands.Paste.Execute(null, editor);
        return true;
    }

    internal static TextBox? FindTextEditor(DependencyObject? element)
    {
        if (element is null) return null;
        if (element is TextBox editor) return editor;
        if (element is not (Visual or Visual3D)) return null;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindTextEditor(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var parent = Parent(node); parent is not null; parent = Parent(parent))
            if (parent is T match) return match;
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
