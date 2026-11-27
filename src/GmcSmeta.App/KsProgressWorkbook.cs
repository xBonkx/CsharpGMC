using ClosedXML.Excel;
using GmcSmeta.Core;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Render the accepted summary snapshot without touching WPF controls
/// or querying SQL. The caller owns the returned workbook.</summary>
internal static class KsProgressWorkbook
{
    internal static XLWorkbook Create(SqlServerActs.SummaryData summary, ContractTotals totals,
        IReadOnlyList<SqlServerActs.ActInfo> acts, string title, DateTime date, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var book = new XLWorkbook();
        try
        {
            var sheet = book.Worksheets.Add("Лист1");
            var matClosed = acts.Sum(a => a.Materials);
            var smrClosed = acts.Sum(a => a.Smr);
            var signed = acts.Where(a => a.Signed).Sum(a => a.Total);
            var headers = new List<string> { "№", "Наименование", "Ед.", "По смете", "Цена", "Сумма по смете" };
            headers.AddRange(acts.Select(a => "№ " + a.Number));
            headers.AddRange(new[] { "Сдано всего", "Сдано, руб.", "Остаток", "Остаток, руб.", "Выдано на объект" });
            sheet.Cell(1, 1).Value = title + " — сводная по выполнению";
            sheet.Cell(1, 1).Style.Font.Bold = true;
            sheet.Cell(1, 1).Style.Font.FontSize = 14;
            sheet.Cell(2, 1).Value = $"на {date:dd.MM.yyyy}";
            sheet.Cell(3, 1).Value =
                $"Материалы: сдано {Money.Format(matClosed)} из {Money.Format(totals.Materials)} " +
                $"({PctOf(matClosed, totals.Materials):0.0}%)   ·   СМР: {Money.Format(smrClosed)} из " +
                $"{Money.Format(totals.Smr)} ({PctOf(smrClosed, totals.Smr):0.0}%)   ·   Всего: " +
                $"{Money.Format(signed)} из {Money.Format(totals.Total)} ({PctOf(signed, totals.Total):0.0}%)";
            XlsxExport.Header(sheet, 5, headers);
            int index = 6;
            var actColumns = acts.Select((act, i) => (act.Id, Column: 7 + i))
                .ToDictionary(a => a.Id, a => a.Column);
            decimal estimateSum = 0m, doneSum = 0m, leftSum = 0m;
            foreach (var row in VisibleSummaryRows(summary))
            {
                token.ThrowIfCancellationRequested();
                if (row.IsSection)
                {
                    sheet.Cell(index, 2).Value = row.Name;
                    sheet.Range(index, 1, index, headers.Count).Style.Font.Bold = true;
                    sheet.Range(index, 1, index, headers.Count).Style.Fill.BackgroundColor =
                        ClosedXML.Excel.XLColor.FromHtml("#EEF2F7");
                    index++;
                    continue;
                }
                sheet.Cell(index, 1).Value = row.Num;
                sheet.Cell(index, 2).Value = row.Name;
                sheet.Cell(index, 3).Value = row.Unit;
                sheet.Cell(index, 4).Value = (double)row.QtyRaw;
                sheet.Cell(index, 5).Value = (double)row.PriceRaw;
                var amount = row.QtyRaw * row.PriceRaw;
                estimateSum += amount;
                doneSum += row.DoneSumRaw;
                leftSum += row.LeftSumRaw;
                sheet.Cell(index, 6).Value = (double)amount;
                foreach (var (actId, quantity) in row.PerAct)
                    if (quantity != 0m && actColumns.TryGetValue(actId, out var column))
                        sheet.Cell(index, column).Value = (double)quantity;
                var offset = 7 + acts.Count;
                sheet.Cell(index, offset).Value = (double)row.DoneRaw;
                sheet.Cell(index, offset + 1).Value = (double)row.DoneSumRaw;
                sheet.Cell(index, offset + 2).Value = (double)row.LeftRaw;
                sheet.Cell(index, offset + 3).Value = (double)row.LeftSumRaw;
                sheet.Cell(index, offset + 4).Value = (double)row.IssuedRaw;
                sheet.Range(index, 4, index, offset + 4).Style.NumberFormat.Format = "#,##0.###";
                sheet.Cell(index, 5).Style.NumberFormat.Format = "#,##0.00";
                sheet.Cell(index, 6).Style.NumberFormat.Format = "#,##0.00";
                sheet.Cell(index, offset + 1).Style.NumberFormat.Format = "#,##0.00";
                sheet.Cell(index, offset + 3).Style.NumberFormat.Format = "#,##0.00";
                if (row.FullyClosed)
                    sheet.Range(index, 1, index, headers.Count).Style.Fill.BackgroundColor =
                        ClosedXML.Excel.XLColor.FromHtml("#DCFCE7");
                index++;
            }
            // как в Python export_summary_excel: Сдано, руб. = 8+n, Остаток, руб. = 10+n
            sheet.Cell(index, 2).Value = "ИТОГО материалы";
            sheet.Cell(index, 6).Value = (double)estimateSum;
            sheet.Cell(index, 8 + acts.Count).Value = (double)doneSum;
            sheet.Cell(index, 10 + acts.Count).Value = (double)leftSum;
            sheet.Range(index, 2, index, headers.Count).Style.Font.Bold = true;
            sheet.Range(index, 6, index, 6).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(index, 8 + acts.Count).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(index, 10 + acts.Count).Style.NumberFormat.Format = "#,##0.00";
            index++;
            sheet.Cell(index, 2).Value = "СМР";
            sheet.Cell(index, 6).Value = (double)totals.Smr;
            for (var i = 0; i < acts.Count; i++)
                sheet.Cell(index, 7 + i).Value = (double)acts[i].Smr;
            sheet.Cell(index, 8 + acts.Count).Value = (double)smrClosed;
            sheet.Cell(index, 10 + acts.Count).Value = (double)(totals.Smr - smrClosed);
            sheet.Range(index, 2, index, headers.Count).Style.Font.Bold = true;
            sheet.Range(index, 6, index, 6).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(index, 8 + acts.Count).Style.NumberFormat.Format = "#,##0.00";
            sheet.Cell(index, 10 + acts.Count).Style.NumberFormat.Format = "#,##0.00";
            index++;
            sheet.Cell(index, 2).Value = "Сумма акта (с НДС)";
            for (var i = 0; i < acts.Count; i++)
            {
                sheet.Cell(index, 7 + i).Value = (double)acts[i].Total;
                sheet.Cell(index, 7 + i).Style.Font.Bold = true;
                sheet.Cell(index, 7 + i).Style.NumberFormat.Format = "#,##0.00";
            }
            sheet.Range(index, 2, index, headers.Count).Style.Font.Bold = true;
            sheet.SheetView.FreezeRows(5);
            sheet.SheetView.FreezeColumns(2);

            token.ThrowIfCancellationRequested();
            sheet.Columns().AdjustToContents(1, Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 200));
            token.ThrowIfCancellationRequested();
            return book;
        }
        catch
        {
            book.Dispose();
            throw;
        }
    }

    private static decimal PctOf(decimal part, decimal whole) =>
        whole == 0m ? 0m : part / whole * 100m;

    private sealed record SummaryExportRow(
        bool IsSection, string Num, string Name, string Unit,
        decimal QtyRaw, decimal PriceRaw, decimal DoneRaw, decimal DoneSumRaw,
        decimal LeftRaw, decimal LeftSumRaw, decimal IssuedRaw, bool FullyClosed,
        IReadOnlyDictionary<long, decimal> PerAct);

    private static IEnumerable<SummaryExportRow> VisibleSummaryRows(SqlServerActs.SummaryData summary)
    {
        foreach (var (system, sectionHeader, positions) in KsWorkspaceProjection.ExportSummaryGroups(summary))
        {
            yield return new SummaryExportRow(true, "", sectionHeader
                ? system.Name : (system.IsSub ? "      ↳  " : "") + system.Name, "", 0m, 0m, 0m, 0m, 0m, 0m, 0m,
                false, new Dictionary<long, decimal>());
            foreach (var p in positions)
                yield return new SummaryExportRow(false, p.NumText, p.Name, p.Unit,
                    p.QtyByKp, p.Price, p.Done, p.DoneSum, p.Left, p.LeftSum, p.Issued,
                    Math.Abs(p.Left) <= 0.000001m, p.PerAct);
        }
    }
}
