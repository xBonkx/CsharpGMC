using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;
using Microsoft.Win32;

namespace GmcSmeta.App;

public partial class ProcurementPane
{
    /// <summary>Python import_sheet: bounded spreadsheet intake, preview and explicit confirmation.</summary>
    private async void ProcurementImportSheet_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;

        var chooser = new OpenFileDialog
        {
            Title = "Импорт контроля оборудования (только безопасное заполнение пустых полей)",
            Filter = "Excel (*.xlsx;*.xlsm)|*.xlsx;*.xlsm"
        };
        if (chooser.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var sheets = await Task.Run(() =>
            {
                using var book = new XLWorkbook(chooser.FileName);
                return book.Worksheets.Select(s => s.Name).ToArray();
            });
            if (sheets.Length == 0) throw new InvalidOperationException("В Excel нет листов.");
            var chosenSheet = sheets.Length == 1 ? sheets[0] : PickSheet(sheets);
            if (chosenSheet is null) return;
            SetStatus("Предварительная проверка Excel без записи в SQL…");
            var rows = await Task.Run(() => ParseSheet(chooser.FileName, chosenSheet));
            if (rows.Count == 0) throw new InvalidOperationException("Не найдены строки закупок.");
            if (rows.Count > 2500)
                throw new InvalidOperationException("За один импорт допускается не более 2500 строк.");
            var preview = await Task.Run(() => SqlServerProcurement.PreviewSheet(
                _connectionString, _contractId, rows));
            if (!await CheckPreviewRevisionAsync(operation, preview.Revision)) return;
            var errors = string.Join("\n", preview.Decisions.Where(x => x.State == "BLOCKED")
                .Take(12).Select(x => $"Строка {x.RowNumber}: {x.Reason}"));
            var summary = $"Лист: {chosenSheet}\n" +
                $"Готово к заполнению: {preview.Ready}\n" +
                $"Без изменений: {preview.Unchanged}\n" +
                $"Заблокировано: {preview.Blocked}\n\n" +
                "Ни позиции сметы, ни количества, ни движения, ни подписанные акты " +
                "не изменяются. Существующие значения не перезаписываются.";
            if (preview.Blocked > 0)
            {
                MessageBox.Show(Window.GetWindow(this),
                    summary + "\n\n" + errors +
                    (preview.Blocked > 12 ? "\n… остальные ошибки не показаны." : "") +
                    "\n\nИмпорт всего листа отменён. Уточните ID позиций или исправьте файл.",
                    "Импорт: блокирующий предпросмотр", MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus($"Импорт отменён: {preview.Blocked} конфликтов, SQL не изменён.");
                return;
            }
            if (preview.Ready == 0)
            {
                MessageBox.Show(Window.GetWindow(this), summary, "Импорт: изменений нет");
                SetStatus("Импорт: нечего изменять.");
                return;
            }
            if (MessageBox.Show(Window.GetWindow(this),
                    summary + "\n\nПрименить подтверждённые изменения одной SQL-транзакцией?",
                    "Импорт: предпросмотр / dry-run",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                SetStatus("Dry-run завершён без записи.");
                return;
            }
            SetStatus($"Применение {preview.Ready} строк (Serializable)…");
            var revision = operation.BeginWrite();
            var saved = await Task.Run(() => SqlServerProcurement.ApplySheet(
                _connectionString, _contractId, rows, revision));
            if (!saved.Saved)
            {
                SetStatus("Импорт не выполнен: " + saved.Error);
                return;
            }

            await TryReloadAsync(preserveStatus: true);
            SetStatus($"Импорт выполнен: {preview.Ready} строк заполнено.");
        }
        catch (Exception ex)
        {
            // A client-side timeout may happen after SQL COMMIT: never retry here.
            _revision = "";
            SetStatus("Операция не подтверждена: " + ex.Message +
                ". Проверьте SQL-данные и ревизию перед повторением.");
            await TryReloadAsync(preserveStatus: true);
        }

    }

    private static string? PickSheet(IReadOnlyList<string> sheets)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var dialog = new Window
        {
            Title = "Выберите лист для импорта",
            Width = 410, Height = 170, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner, ShowInTaskbar = false
        };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = "Лист контрольной таблицы:", Margin = new Thickness(0, 0, 0, 8) });
        var combo = new ComboBox { ItemsSource = sheets, SelectedIndex = 0, MinHeight = 28 };
        panel.Children.Add(combo);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Выбрать", MinWidth = 90, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { dialog.DialogResult = true; };
        var cancel = new Button { Content = "Отмена", MinWidth = 90 };
        cancel.Click += (_, _) => { dialog.DialogResult = false; };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? combo.SelectedItem as string : null;
    }

    private static List<SqlServerProcurement.SheetRow> ParseSheet(string path, string sheetName)
    {
        using var workbook = new XLWorkbook(path);
        var sheet = workbook.Worksheet(sheetName);
        var range = sheet.RangeUsed() ?? throw new FormatException("Пустой лист.");
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var cell in range.Row(1).Cells())
        {
            var header = cell.GetString().Trim().ToLowerInvariant();
            var key = header switch
            {
                "position_id" or "id позиции" or "id закупочной позиции" => "id",
                "system" or "система" or "система/раздел" => "system",
                "name" or "наименование" or "наименование оборудования" => "name",
                "unit" or "ед." or "ед. изм." or "единица измерения" => "unit",
                "qty" or "кол-во" or "количество" => "qty",
                _ when header.Contains("поставщик") => "supplier",
                _ when header.Contains("статус") || header == "этап" => "stage",
                _ when header.Contains("заявка") => "order",
                _ when header.Contains("счёт") || header.Contains("счет") => "invoice",
                _ when header.Contains("закупочная цена") || header.Contains("цена закупки") => "cost",
                _ when header.Contains("срок поставки") || header == "дата поставки" => "due",
                _ when header.Contains("замена") => "replacement",
                _ when header.Contains("примечание") || header.Contains("комментарий") => "note",
                _ => ""
            };
            if (key.Length > 0 && !columns.TryAdd(key, cell.Address.ColumnNumber))
                throw new FormatException($"Повтор колонки «{key}».");
        }
        foreach (var required in new[] { "system", "name", "unit", "qty" })
            if (!columns.ContainsKey(required))
                throw new FormatException($"Нет обязательной колонки «{required}».");
        string Get(IXLRangeRow row, string key) =>
            columns.TryGetValue(key, out var col) ? row.Cell(col).GetString().Trim() : "";
        decimal ParseMoney(string text, string label, int rowNumber)
        {
            var clean = text.Replace(" ", "").Replace("\u00a0", "").Replace(',', '.');
            if (!decimal.TryParse(clean, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Строка {rowNumber}: некорректное значение {label}.");
            return value;
        }
        var result = new List<SqlServerProcurement.SheetRow>();
        foreach (var row in range.Rows().Skip(1))
        {
            var name = Get(row, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var nr = row.RowNumber();
            long? id = null;
            var idText = Get(row, "id");
            if (idText.Length > 0)
            {
                if (!long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
                    throw new FormatException($"Строка {nr}: неверный ID позиции.");
                id = value;
            }
            var qty = ParseMoney(Get(row, "qty"), "количество", nr);
            decimal? cost = null;
            if (Get(row, "cost") is var rawCost && rawCost.Length > 0)
                cost = ParseMoney(rawCost, "цена", nr);
            DateTime? due = null;
            var dateText = Get(row, "due");
            if (dateText.Length > 0)
            {
                if (!DateTime.TryParse(dateText, CultureInfo.GetCultureInfo("ru-RU"),
                    DateTimeStyles.None, out var parsed))
                    throw new FormatException($"Строка {nr}: неверная дата поставки.");
                due = parsed.Date;
            }
            result.Add(new SqlServerProcurement.SheetRow(
                id, Get(row, "system"), name, Get(row, "unit"), qty,
                Get(row, "supplier"), Get(row, "stage"), Get(row, "order"),
                Get(row, "invoice"), cost, due, Get(row, "replacement"), Get(row, "note")));
        }
        return result;
    }
}
