using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core.Db;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Procurement control XLSX/PDF exports and workshop PDF from SQL contract scope.</summary>
public partial class ProcurementPane
{
    private async void ProcurementExport_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Подготовка выгрузки контроля…");
        ProcurementControlExport.Snapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => ProcurementControlExport.Read(_connectionString, _contractId));
        }
        catch (Exception ex)
        {
            SetStatus("Ошибка чтения данных контроля: " + ex.Message);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Выгрузка контроля оборудования",
            Filter = "Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf",
            FileName = $"Контроль оборудования — {snapshot.Document.ObjectName} — {DateTime.Today:dd.MM.yyyy}.xlsx",
            AddExtension = true, OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var path = dialog.FileName;
        try
        {
            if (dialog.FilterIndex == 2)
            {
                var data = await Task.Run(() => ProcurementPdfExport.CreateControl(snapshot, withMoney: false));
                File.WriteAllBytes(path, data);
            }
            else
            {
                using var workbook = await Task.Run(() =>
                    ProcurementControlExport.CreateWorkbook(snapshot, withMoney: false));
                workbook.SaveAs(path);
            }
            SetStatus("Контроль оборудования сохранён: " + path);
            TryOpenExport(path);
        }
        catch (Exception ex) { SetStatus("Экспорт не выполнен: " + ex.Message); }
    }

    private async void ProcurementShopOrder_Click(object sender, RoutedEventArgs e)
    {
        using var operation = TryBeginOperation();
        if (operation is null) return;
        var selectedIds = SelectedBuyingIds().ToHashSet();

        SetStatus("Подготовка заявки в цех…");
        try
        {
            var snapshot = await Task.Run(() =>
            {
                using var conn = SqlServerDatabase.Open(_connectionString);
                var revision = SqlServerDatabase.Revision(conn);
                var info = ProcurementRequestPlanner.ReadDocumentInfo(_connectionString, _contractId);
                var rows = SqlServerProcurement.BuyRows(_connectionString, _contractId);
                if (SqlServerDatabase.Revision(conn) != revision)
                    throw new InvalidOperationException("Закупки изменились во время чтения. Повторите формирование заявки.");
                var positions = ProcurementRequestPlanner.SelectShopRows(rows, selectedIds);
                var selectedOnly = positions.Any(p => selectedIds.Contains(p.PositionId));
                return (info, positions, selectedOnly);
            });
            if (snapshot.positions.Count == 0)
            {
                SetStatus("Нет позиций для цеха: назначьте статус «Заявка в цех».");
                return;
            }
            var preview = new ProcurementShopOrderPreview(snapshot.info,
                ProcurementRequestPlanner.ShopPrintRows(snapshot.positions), snapshot.selectedOnly)
            { Owner = Window.GetWindow(this) };
            if (preview.ShowDialog() != true) { SetStatus("Печать заявки отменена."); return; }
            var file = new SaveFileDialog
            {
                Title = "Заявка в цех",
                Filter = "PDF (*.pdf)|*.pdf",
                DefaultExt = ".pdf",
                FileName = $"Заявка в цех — {snapshot.info.ObjectName} — {DateTime.Today:dd.MM.yyyy}.pdf",
                AddExtension = true,
                OverwritePrompt = true
            };
            if (file.ShowDialog(Window.GetWindow(this)) != true) { SetStatus("Печать заявки отменена."); return; }
            var lines = preview.Lines;
            var number = preview.Number;
            var due = preview.Due;
            var bytes = await Task.Run(() =>
                ProcurementPdfExport.CreateShopOrder(snapshot.info, lines, number, due));
            File.WriteAllBytes(file.FileName, bytes);
            SetStatus($"Заявка в цех сохранена: {file.FileName}");
            TryOpenExport(file.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("Заявка в цех не сохранена: " + ex.Message);
        }

    }

    private static void TryOpenExport(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* Saved file is valid even if there is no associated viewer. */ }
    }
}
