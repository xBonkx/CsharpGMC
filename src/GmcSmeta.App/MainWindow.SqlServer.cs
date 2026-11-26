using System.Windows;
using ClosedXML.Excel;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

public partial class MainWindow
{
    // Until the SQL Server integration suite is ready, all C# workspace writes are pilot-only.
    private bool SqlWritesEnabled => _serverConn is not null &&
        SqlSchemaCompatibility.IsWritablePilot(_serverConn.Database, _serverConn.DataSource);

    private void EditSqlServerObjectProperties(int? id)
    {
        if (_serverConn is null || _sqlConnectionString is null) return;
        if (!SqlWritesEnabled)
            throw new InvalidOperationException("Изменения разрешены только в GmcSmetaPilot_*.");

        // Read revision before opening the dialog to detect concurrent writes during editing.
        var revision = SqlServerDatabase.Revision(_serverConn);
        var previous = id is int objectId
            ? SqlServerWorkspace.ReadObject(_serverConn, objectId) : null;
        if (id is int existing && _objects.Any(o => o.Id == existing && o.Archived))
            throw new InvalidOperationException("Архивный объект нельзя изменять.");

        var dialog = new ObjectDialog(
            SqlServerWorkspace.ObjectTags(_serverConn, previous?.Form ?? ""), previous)
            { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Values is not { } values) return;

        var saved = SaveSqlServerObjectProperties(values, id, revision);
        ReloadAndSelect(saved);
    }

    private int SaveSqlServerObjectProperties(ObjectProperties values, int? id,
        string? expectedRevision = null)
    {
        if (_serverConn is null || _sqlConnectionString is null || !SqlWritesEnabled)
            throw new InvalidOperationException("Доступна только тестовая SQL Server база GmcSmetaPilot_*.");
        var revision = expectedRevision ?? SqlServerDatabase.Revision(_serverConn);
        if (id is int existing)
        {
            SqlServerWorkspace.UpdateObject(_sqlConnectionString, existing, values, revision);
            return existing;
        }
        return checked((int)SqlServerWorkspace.CreateObject(_sqlConnectionString, values, revision));
    }

    private async void NewSqlServerContractFlow(int objectId, string kind, long? baseId, long? copyFrom)
    {
        if (_serverConn is null || _sqlConnectionString is null) return;
        var obj = _objects.FirstOrDefault(o => o.Id == objectId);
        if (obj is null) return;
        try
        {
            if (!SqlWritesEnabled)
                throw new InvalidOperationException("Изменения разрешены только в GmcSmetaPilot_*.");
            if (obj.Archived)
                throw new InvalidOperationException("Архивный объект нельзя изменять.");

            var revision = SqlServerDatabase.Revision(_serverConn);
            var contracts = _docs.Where(d => d.ObjectId == objectId)
                .Select(d => (Id: (long)d.Id, d.Title, d.Kind)).ToList();
            var dialog = new ContractDialog(obj.Name, contracts,
                SqlServerWorkspace.Companies(_serverConn),
                SqlServerWorkspace.ObjectTags(_serverConn), kind, baseId, copyFrom,
                SqlServerWorkspace.LastCompanyId(_serverConn, objectId), allowCopy: true,
                allowExcelImport: kind == "kp" && baseId is null && copyFrom is null)
                { Owner = this };
            if (dialog.ShowDialog() != true) return;
            if (dialog.ImportExcelRequested)
            {
                await ImportExcelForObjectAsync(objectId, newDocumentsOnly: true);
                return;
            }
            if (dialog.Result is not { } value) return;

            var data = new SqlServerWorkspace.NewContractData(value.Kind, value.Number, value.Date,
                value.Name, value.CompanyId, value.BaseContractId);
            var created = value.CopyFrom is long sourceId
                ? SqlServerWorkspace.CopyContract(_sqlConnectionString, sourceId, data, revision)
                : SqlServerWorkspace.CreateEmptyContract(_sqlConnectionString, objectId, data, revision);
            ReloadAndSelect(objectId);
            OpenEstimate(checked((int)created));
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось создать документ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SaveSqlServerObjectRename(int objectId, string name)
    {
        if (_serverConn is null || _sqlConnectionString is null || !SqlWritesEnabled)
            throw new InvalidOperationException("Доступна только тестовая SQL Server база GmcSmetaPilot_*.");
        if (_objects.Any(o => o.Id == objectId && o.Archived))
            throw new InvalidOperationException("Архивный объект нельзя переименовать.");
        var revision = SqlServerDatabase.Revision(_serverConn);
        var current = SqlServerWorkspace.ReadObject(_serverConn, objectId);
        SqlServerWorkspace.UpdateObject(_sqlConnectionString, objectId,
            current with { Name = name }, revision);
    }

    /// <summary>Смета документа в Excel (kp_export): разделы, строки, количества, цены, суммы.</summary>
    private void ExportEstimate(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            var estimate = EstimateReader.Load(_serverConn, doc.Id);
            var title = estimate.Title;
            XlsxExport.Save(this, $"Смета — {title}", sheet =>
            {
                sheet.Cell(1, 1).Value = title;
                sheet.Cell(1, 1).Style.Font.Bold = true;
                if (!string.IsNullOrWhiteSpace(estimate.Company))
                {
                    sheet.Cell(2, 1).Value = estimate.Company;
                    sheet.Cell(2, 1).Style.Font.Italic = true;
                }
                XlsxExport.Header(sheet, 4, new[] { "№", "Наименование", "Ед.", "Кол-во", "Цена", "Сумма" });
                var row = 5;
                foreach (var r in estimate.Rows)
                {
                    if (r.IsSystem)
                    {
                        var cell = sheet.Cell(row, 2);
                        cell.Value = r.Name;
                        cell.Style.Font.Bold = true;
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F6FD");
                        row++;
                        continue;
                    }
                    sheet.Cell(row, 1).Value = r.Num;
                    sheet.Cell(row, 2).Value = r.Name;
                    sheet.Cell(row, 3).Value = r.Unit;
                    sheet.Cell(row, 4).Value = (double)r.Qty;
                    if (r.Price != 0m) sheet.Cell(row, 5).Value = (double)r.Price;
                    if (r.Amount != 0m) sheet.Cell(row, 6).Value = (double)r.Amount;
                    sheet.Range(row, 5, row, 6).Style.NumberFormat.Format = "#,##0.00";
                    row++;
                }
                var totals = estimate.Totals;
                row++;
                foreach (var (label, value, bold) in new[]
                {
                    ("Материалы", totals.Materials, true),
                    ("СМР", totals.Smr, false),
                    ("ИТОГО", totals.Total, true),
                })
                {
                    sheet.Cell(row, 5).Value = label;
                    sheet.Cell(row, 5).Style.Font.Bold = bold;
                    sheet.Cell(row, 6).Value = (double)value;
                    sheet.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
                    sheet.Cell(row, 6).Style.Font.Bold = bold;
                    row++;
                }
                sheet.Columns(1, 6).AdjustToContents();
                sheet.Column(2).Width = 55;
            });
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Экспорт сметы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Закупки — контроль оборудования по документу (proc_view).</summary>
    private void OpenProcurement(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            new ProcurementWindow(_sqlConnectionString, doc.Id, doc.Title) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Закупки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Выдача на объект — окно ведомостей (ship_view).</summary>
    private void OpenShipments(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            var objectId = _docs.FirstOrDefault(d => d.Id == doc.Id)?.ObjectId ?? doc.ObjectId;
            new ShipmentsWindow(_sqlConnectionString, objectId, doc.Title) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Выдача на объект", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>КС-2 — окно актов документа (ks_view).</summary>
    private void OpenActs(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            new ActsWindow(_sqlConnectionString, doc.Id, doc.Title) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "КС-2", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Сравнить документ с другим КП/договором объекта (compare_view).</summary>
    private void OpenCompare(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            var docs = _docs.Where(d => d.ObjectId == doc.ObjectId)
                .OrderBy(d => d.Order).ThenBy(d => d.Id)
                .Select(d => (Id: (int)d.Id, d.Title)).ToList();
            if (docs.Count < 2)
            {
                StatusText.Text = "В объекте один документ — сравнивать не с чем.";
                return;
            }
            var dialog = new CompareDialog(_sqlConnectionString, doc.Id, docs) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Сравнение", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Окно «Этапы»: разбивка КП на 100 % работы; открытие КП выбранного этапа.</summary>
    private void OpenStages(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            var dialog = new StagesDialog(_sqlConnectionString, doc.Id, doc.Title) { Owner = this };
            dialog.OpenStageRequested += stageId =>
            {
                ReloadAndSelect(doc.ObjectId);
                OpenEstimate(stageId);
            };
            dialog.ShowDialog();
            ReloadAndSelect(doc.ObjectId);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Этапы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Open a stage from the embedded estimate stage card. The existing main
    /// window remains the sole owner of navigation and document selection.
    /// </summary>
    internal void NavigateToStageEstimate(int stageId, int objectId)
    {
        if (_serverConn is null) return;
        ReloadAndSelect(objectId);
        OpenEstimate(stageId);
    }

    /// <summary>Скопировать расценки из другого КП (rate_transfer_dialog): диалог, перенос, перечитывание.</summary>
    private void TransferRates(DocRow doc)
    {
        try
        {
            if (_serverConn is null || _sqlConnectionString is null) return;
            var dialog = new RateTransferDialog(_sqlConnectionString, doc.Id, doc.Title) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ResultData is not { } result) return;

            ReloadAndSelect(doc.ObjectId);
            OpenEstimate(doc.Id);
            StatusText.Text = $"Перенесено расценок: {result.Prices}. Отменить — Ctrl+Z";
            var estimateSmr = Totals.ReadOne(_serverConn, doc.Id)?.Smr ?? 0m;
            if (Math.Abs((decimal)result.WorkTotal - estimateSmr) >= 0.01m)
                MessageBox.Show(this,
                    $"СМР работ в смете ({Money.Format((decimal)result.WorkTotal)}) отличается от итога сметы " +
                    $"({Money.Format(estimateSmr)}). Проверьте строку «СМР» в итогах.",
                    "СМР после переноса", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Перенос расценок",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Свойства документа в SQL Server-режиме — эталон _edit_document_properties:
    /// подписанные документы и этапы не открываются, НДС меняется вместе с организацией.
    /// </summary>
    private void EditSqlServerContractProperties(DocRow doc, bool makeContract)
    {
        if (_serverConn is null || _sqlConnectionString is null) return;
        try
        {
            if (!SqlWritesEnabled)
                throw new InvalidOperationException("Изменения разрешены только в GmcSmetaPilot_*.");
            if (_objects.Any(o => o.Id == doc.ObjectId && o.Archived))
                throw new InvalidOperationException("Архивный объект нельзя изменять.");

            // Ревизия читается до диалога: правка другим сотрудником во время набора отвергается.
            var revision = SqlServerDatabase.Revision(_serverConn);
            var info = SqlServerWorkspace.ReadContract(_serverConn, doc.Id);
            if (info.Locked || info.StageOf is not null)
            {
                // Python в этом случае просто не открывает свойства.
                StatusText.Text = info.Locked
                    ? "Документ подписан — свойства недоступны."
                    : "Это этап другого документа — реквизиты меняются у полного КП.";
                return;
            }

            var objectName = _objects.FirstOrDefault(o => o.Id == doc.ObjectId)?.Name ?? "";
            var mains = _docs.Where(d => d.ObjectId == doc.ObjectId && d.Kind is "main" or "kp")
                .Select(d => ((long)d.Id, d.Title)).ToList();
            var dialog = new ContractDialog(objectName, info, SqlServerWorkspace.Companies(_serverConn),
                SqlServerWorkspace.ObjectTags(_serverConn, info.Form), mains, makeContract) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Result is not { } value) return;

            // Python: make_contract при выборе «КП» превращает документ в договор.
            var kind = makeContract && value.Kind == "kp" ? "main" : value.Kind;
            var changed = SqlServerWorkspace.UpdateContract(_sqlConnectionString, doc.Id,
                new ContractProperties(kind, value.Number, value.Date, value.Name, value.CompanyId,
                    value.BaseContractId, value.VatRate, value.Note, value.Form), revision);

            if (!changed) return;      // как в эталоне: без изменений страница не перечитывается
            ReloadAndSelect(doc.ObjectId);
            OpenEstimate(doc.Id);
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(this, ex.Message, "Не удалось сохранить документ",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
