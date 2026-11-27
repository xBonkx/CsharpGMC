using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using Microsoft.Win32;

namespace GmcSmeta.App;

/// <summary>Procurement control XLSX/PDF exports and workshop PDF from SQL contract scope.</summary>
public partial class ProcurementPane
{
    private bool _controlExportBusy;

    private async void ProcurementExport_Click(object sender, RoutedEventArgs e)
    {
        if (_controlExportBusy || !IsLoaded) return;
        _controlExportBusy = true;
        var attachment = _attachmentGeneration;
        bool IsCurrent() => IsLoaded && IsVisible && attachment == _attachmentGeneration;
        SetStatus("Подготовка выгрузки контроля…");
        try
        {
            var snapshot = await Task.Run(() => ProcurementControlExport.Read(_connectionString, _contractId));
            if (!IsCurrent()) return;
            var dialog = new SaveFileDialog
            {
                Title = "Выгрузка контроля оборудования",
                Filter = "Excel (*.xlsx)|*.xlsx|PDF (*.pdf)|*.pdf",
                FileName = $"Контроль оборудования — {snapshot.Document.ObjectName} — {DateTime.Today:dd.MM.yyyy}.xlsx",
                AddExtension = true, OverwritePrompt = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true || !IsCurrent()) return;
            var path = dialog.FileName;
            var pdf = dialog.FilterIndex == 2;
            // Once the user confirms a destination, finish this captured
            // report independently of later view switches, using atomic save.
            await Task.Run(() =>
            {
                if (pdf)
                {
                    var data = ProcurementPdfExport.CreateControl(snapshot, withMoney: false);
                    ExportFilePublication.WriteBytes(path, data);
                }
                else
                {
                    using var workbook = ProcurementControlExport.CreateWorkbook(snapshot, withMoney: false);
                    ExportFilePublication.Write(path, stream => workbook.SaveAs(stream));
                }
            });
            if (IsCurrent())
            {
                SetStatus("Контроль оборудования сохранён: " + path);
                TryOpenExport(path);
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent()) SetStatus("Экспорт не выполнен: " + ex.Message);
        }
        finally { _controlExportBusy = false; }
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
                var report = ProcurementRequestPlanner.ReadReport(_connectionString, _contractId,
                    ProcurementRequestPlanner.ReportKind.Shop, operation.Revision);
                var positions = ProcurementRequestPlanner.SelectShopRows(report.Rows, selectedIds);
                var selectedOnly = positions.Any(p => selectedIds.Contains(p.PositionId));
                return (info: report.Document, positions, selectedOnly);
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
            var destination = file.FileName;
            await Task.Run(() =>
            {
                var bytes = ProcurementPdfExport.CreateShopOrder(snapshot.info, lines, number, due);
                ExportFilePublication.WriteBytes(destination, bytes);
            });
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
