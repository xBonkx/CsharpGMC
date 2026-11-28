using System.Threading.Tasks;
using System.Windows;
using GmcSmeta.Core.Db;

namespace GmcSmeta.App;

public partial class CostSummaryPane
{
    private async void MaterialSupplier_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedMaterials();
        if (ids.Count == 0) { SetStatus("Выделите позиции или группу."); return; }
        var prompt = new PromptDialog("Поставщик для выбранных",
            $"Поставщик для {ids.Count} позиций (пусто — убрать поставщика):", "")
        {
            Owner = Window.GetWindow(this)
        };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var value = prompt.Value;
            var result = await Task.Run(() => SqlServerCostOperations.SetSupplierBatch(
                _connectionString, _contractId, ids, value, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Поставщик изменён у {result.Changed} позиций.");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось назначить поставщика: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void MaterialNote_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedMaterials();
        if (ids.Count == 0) { SetStatus("Выделите позиции или группу."); return; }
        var prompt = new PromptDialog("Внутренние пометки",
            $"Общая пометка для {ids.Count} строк (пусто — очистить):", "")
        {
            Owner = Window.GetWindow(this)
        };
        if (prompt.ShowDialog() != true) return;
        try
        {
            var value = prompt.Value;
            var result = await Task.Run(() => SqlServerCostOperations.SetCostFormatting(
                _connectionString, _contractId, ids, "cost_note", value, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Пометки изменены: {result.Changed} позиций.");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось изменить пометки: " + ex.Message);
            await ReloadAsync();
        }
    }

    private async void MaterialClear_Click(object sender, RoutedEventArgs e)
    {
        using var operation = BeginCostOperation();
        if (operation is null) return;
        var ids = SelectedMaterials();
        if (ids.Count == 0) { SetStatus("Выделите позиции или группу."); return; }
        var choice = MessageBox.Show(Window.GetWindow(this),
            "Что очистить у выбранных позиций?\nДа — цену клиенту (розница).\n" +
            "Нет — закупочную цену.\nОтмена — ничего не менять.",
            "Очистка материалов", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (choice == MessageBoxResult.Cancel) return;
        var field = choice == MessageBoxResult.Yes ? "price" : "purchase_price";
        try
        {
            var result = await Task.Run(() => SqlServerCostOperations.ClearMaterialCells(
                _connectionString, _contractId, ids, field, ServerRevision()));
            _revision = result.Revision;
            await ReloadAsync();
            SetStatus($"Очищено: {result.Changed}, пропущено: {result.Skipped} позиций.");
        }
        catch (Exception ex)
        {
            SetStatus("Не удалось очистить цены: " + ex.Message);
            await ReloadAsync();
        }
    }

    /// <summary>Internal cost workbook, never customer-facing KP export.</summary>
    private void MaterialsExport_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseSnapshot) return;
        var visible = MaterialsGrid.Items.OfType<CostSummaryPreviewRow>().ToArray();
        XlsxExport.Save(Window.GetWindow(this), "Материалы — себестоимость " + _contractId, sheet =>
        {
            XlsxExport.Header(sheet, 1, new[] { "Наименование", "Ед.", "Кол-во",
                "Розница за ед.", "Розница итого", "Закуп за ед.", "Закуп итого",
                "Наценка", "Маржа", "Поставщик", "Пометки" });
            var index = 2;
            foreach (var r in visible)
            {
                sheet.Cell(index, 1).Value = r.Name;
                sheet.Cell(index, 2).Value = r.Unit;
                sheet.Cell(index, 3).Value = r.Qty;
                sheet.Cell(index, 4).Value = r.UPrice;
                sheet.Cell(index, 5).Value = r.MSell;
                sheet.Cell(index, 6).Value = r.UCost;
                sheet.Cell(index, 7).Value = r.MCost;
                sheet.Cell(index, 8).Value = r.MatMarkupPct;
                sheet.Cell(index, 9).Value = r.MatMargin;
                sheet.Cell(index, 10).Value = r.Supplier;
                sheet.Cell(index, 11).Value = r.CostNote;
                if (r.IsGroup)
                    sheet.Row(index).Style.Font.Bold = true;
                index++;
            }
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(14, 75);
        });
    }

    private void SmrExport_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUseSnapshot) return;
        var work = SmrWorkGrid.Items.OfType<CostSummaryPreviewRow>().ToArray();
        var expenses = SmrExpenseGrid.Items.OfType<SmrExpensePreviewRow>().ToArray();
        XlsxExport.Save(Window.GetWindow(this), "Монтаж СМР " + _contractId, sheet =>
        {
            XlsxExport.Header(sheet, 1, new[] { "Наименование", "Ед.", "Кол-во",
                "Закуп работ за ед.", "Закуп работ итого", "Розница работ за ед.",
                "Розница работ итого", "Маржа работ", "Наценка %" });
            var row = 2;
            foreach (var r in work)
            {
                sheet.Cell(row, 1).Value = r.Name;
                sheet.Cell(row, 2).Value = r.Unit;
                sheet.Cell(row, 3).Value = r.Qty;
                sheet.Cell(row, 4).Value = r.UWcost;
                sheet.Cell(row, 5).Value = r.WCost;
                sheet.Cell(row, 6).Value = r.UWsell;
                sheet.Cell(row, 7).Value = r.WSell;
                sheet.Cell(row, 8).Value = r.WorkMargin;
                sheet.Cell(row, 9).Value = r.MarkupPct;
                row++;
            }
            sheet.SheetView.FreezeRows(1);
            sheet.Columns().AdjustToContents(14, 75);
            var extra = sheet.Workbook.Worksheets.Add("Расходы на монтаж");
            XlsxExport.Header(extra, 1, new[] { "Дата", "Статья", "Кому", "Сумма", "Примечание" });
            row = 2;
            foreach (var x in expenses)
            {
                extra.Cell(row, 1).Value = x.Date;
                extra.Cell(row, 2).Value = x.Kind;
                extra.Cell(row, 3).Value = x.Who;
                extra.Cell(row, 4).Value = x.Amount;
                extra.Cell(row, 5).Value = x.Note;
                row++;
            }
            extra.Columns().AdjustToContents(12, 75);
        });
    }
}
