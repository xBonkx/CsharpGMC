using System.Windows;
using System.Windows.Input;

namespace GmcSmeta.App;

/// <summary>A single, non-default destructive confirmation for selected estimate rows.</summary>
public partial class DeletePositionsDialog : Window
{
    public DeletePositionsDialog(IReadOnlyList<string> selectedNames, int additionalComponents)
    {
        if (selectedNames is null || selectedNames.Count == 0)
            throw new ArgumentException("Нет позиций для подтверждения.", nameof(selectedNames));
        InitializeComponent();
        var count = selectedNames.Count;
        Heading.Text = count == 1 ? "Удалить позицию?" : $"Удалить {count} позиций?";
        Description.Text = count == 1
            ? "Выбранная строка будет удалена из сметы."
            : $"Будут удалены все {count} выделенных позиций одной операцией.";
        NamesList.ItemsSource = selectedNames.Take(8)
            .Select(name => string.IsNullOrWhiteSpace(name) ? "(без названия)" : name.Trim())
            .Concat(count > 8 ? new[] { $"…и ещё {count - 8}" } : Array.Empty<string>())
            .ToArray();
        ComponentsNote.Text = additionalComponents > 0
            ? $"Дополнительно будут удалены комплектующие: {additionalComponents}."
            : "Удаление затронет только выбранные позиции.";
        DeleteButton.Content = count == 1 ? "Удалить позицию" : $"Удалить {count} позиций";
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Delete_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
