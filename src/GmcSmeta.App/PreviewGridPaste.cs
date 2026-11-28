using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace GmcSmeta.App;

/// <summary>Rectangular clipboard paste for detached procurement previews only.
/// No structural row creation, formula evaluation, SQL or partial validation writes.</summary>
internal static class PreviewGridPaste
{
    internal static void Install<T>(DataGrid grid, params string[] editableProperties) where T : class
    {
        grid.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control ||
                Keyboard.FocusedElement is TextBoxBase) return;
            if (GridEditInteraction.TryPasteInEditingCell(grid)) { e.Handled = true; return; }
            e.Handled = true;
            try
            {
                if (!Clipboard.ContainsText()) return;
                if (!TryApply<T>(grid, Clipboard.GetText(), editableProperties, out var error))
                    MessageBox.Show(Window.GetWindow(grid), error, "Вставка диапазона", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                MessageBox.Show(Window.GetWindow(grid), "Буфер обмена занят. Повторите вставку.",
                    "Вставка диапазона", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
    }

    internal static bool TryApply<T>(DataGrid grid, string text, IReadOnlyCollection<string> editableProperties,
        out string error) where T : class
    {
        error = "Выберите начальную ячейку диапазона.";
        if (!grid.CurrentCell.IsValid || grid.IsReadOnly || !grid.IsEnabled ||
            grid.SelectionUnit != DataGridSelectionUnit.Cell) return false;
        if (!TryParse(text, out var values)) { error = "Нужен прямоугольный диапазон до 10 000 ячеек."; return false; }
        if (!grid.CommitEdit(DataGridEditingUnit.Cell, true) || !grid.CommitEdit(DataGridEditingUnit.Row, true))
        { error = "Сначала исправьте текущую ячейку."; return false; }
        var anchor = grid.CurrentCell;
        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).OrderBy(c => c.DisplayIndex).ToArray();
        int col = Array.IndexOf(columns, anchor.Column), row = grid.Items.IndexOf(anchor.Item);
        if (row < 0 || col < 0 || row + values.Count > grid.Items.Count || col + values[0].Count > columns.Length)
        { error = "Диапазон выходит за пределы таблицы."; return false; }

        // Every target and value is checked before modifying any detached model row.
        var targets = new List<(T Row, PropertyInfo Property, object? Value)>();
        for (int y = 0; y < values.Count; y++)
        {
            if (grid.Items[row + y] is not T item) return false;
            for (int x = 0; x < values[y].Count; x++)
            {
                if (columns[col + x] is not DataGridTextColumn column || column.IsReadOnly ||
                    column.Binding is not Binding binding || binding.Mode == BindingMode.OneWay ||
                    binding.Path?.Path is not string propertyName || !editableProperties.Contains(propertyName))
                { error = "В диапазоне есть недоступная для редактирования колонка."; return false; }
                var property = typeof(T).GetProperty(propertyName);
                if (property?.SetMethod is not { IsPublic: true } || property.GetIndexParameters().Length != 0)
                { error = "Колонка не поддерживает вставку."; return false; }
                var input = values[y][x];
                object? value;
                if (property.PropertyType == typeof(string)) value = input;
                else if (property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?))
                {
                    if (input.Trim().Length == 0 && property.PropertyType == typeof(decimal?)) value = null;
                    else if (decimal.TryParse(input.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.'),
                        NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) &&
                        number >= 0m && number <= GmcSmeta.Core.NumberText.MaxNumber) value = number;
                    else { error = $"Некорректное число: строка {y + 1}, колонка {x + 1}."; return false; }
                }
                else { error = "Колонка не поддерживает вставку."; return false; }
                targets.Add((item, property, value));
            }
        }

        // Only public auto-properties of the three detached preview DTOs are allowed.
        // Setters do not write to storage or invoke application commands.
        foreach (var target in targets) target.Property.SetValue(target.Row, target.Value);
        grid.Items.Refresh();
        grid.SelectedCells.Clear();
        foreach (var targetRow in Enumerable.Range(row, values.Count))
            foreach (var targetColumn in columns.Skip(col).Take(values[0].Count))
                grid.SelectedCells.Add(new DataGridCellInfo(grid.Items[targetRow], targetColumn));
        if (grid.Items.Contains(anchor.Item)) grid.CurrentCell = anchor;
        error = "";
        return true;
    }

    // Excel TSV permits quoted fields with escaped quotes and embedded newlines.
    internal static bool TryParse(string text, out List<List<string>> rows)
    {
        rows = new();
        if (string.IsNullOrEmpty(text) || text.Length > 1_000_000) return false;
        var fields = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, closed = false, atStart = true;
        int cells = 0;
        bool FinishField()
        {
            if (++cells > 10000) return false;
            fields.Add(field.ToString()); field.Clear(); closed = false; atStart = true;
            return true;
        }
        // Capture the list locally: C# does not allow capturing an out parameter.
        var result = rows;
        bool FinishRow()
        {
            if (!FinishField()) return false;
            if (result.Count > 0 && fields.Count != result[0].Count) return false;
            result.Add(fields); fields = new List<string>(); return true;
        }
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') { quoted = false; closed = true; }
                else field.Append(ch);
                continue;
            }
            if (ch == '\t') { if (!FinishField()) return false; continue; }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                if (!FinishRow()) return false;
                continue;
            }
            if (closed) return false;
            if (atStart && ch == '"') { quoted = true; atStart = false; continue; }
            atStart = false;
            field.Append(ch);
        }
        if (quoted) return false;
        if (fields.Count != 0 || field.Length != 0 || !atStart || closed)
            if (!FinishRow()) return false;
        return rows.Count > 0 && rows[0].Count > 0;
    }
}
