using System.Data;
using System.Globalization;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.App;

/// <summary>One SQL snapshot for the Python procurement-control Excel/PDF exports.</summary>
public static class ProcurementControlExport
{
    public sealed record Snapshot(
        ProcurementRequestPlanner.DocumentInfo Document,
        IReadOnlyList<SqlServerProcurement.BuyRow> Rows,
        IReadOnlyDictionary<long, string> IssueDates);

    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public static Snapshot Read(string connectionString, int contractId)
    {
        var document = ProcurementRequestPlanner.ReadDocumentInfo(connectionString, contractId);
        var positions = SqlServerProcurement.BuyRows(connectionString, contractId);
        var issues = new Dictionary<long, List<string>>();
        using var conn = SqlServerDatabase.Open(connectionString);
        using var cmd = new SqlCommand(
            "SELECT m.position_id,m.qty,m.[date] FROM dbo.movements m " +
            "JOIN dbo.positions p ON p.id=m.position_id " +
            "JOIN dbo.systems sy ON sy.id=p.system_id " +
            "WHERE sy.contract_id=@contract AND m.kind=N'out' " +
            "ORDER BY m.position_id,m.[date],m.id", conn);
        cmd.Parameters.Add("@contract", SqlDbType.BigInt).Value = contractId;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
            var qty = Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture);
            var date = reader.IsDBNull(2) ? "б/д" :
                reader.GetDateTime(2).ToString("dd.MM.yy", Ru);
            if (!issues.TryGetValue(id, out var parts))
                issues[id] = parts = new List<string>();
            parts.Add(qty.ToString("0.######", Ru) + " (" + date + ")");
        }
        return new Snapshot(document, positions,
            issues.ToDictionary(kv => kv.Key, kv => string.Join(", ", kv.Value)));
    }

    public static XLWorkbook CreateWorkbook(Snapshot snapshot, bool withMoney = false)
    {
        var book = new XLWorkbook();
        var ws = book.Worksheets.Add("Контроль");
        var headers = new List<string>
        {
            "Поз.", "Наименование", "Ед.", "По КП", "Поставщик", "Статус",
            "Заказ / счёт", "Пришло", "Выдано", "Выдачи (кол-во, дата)",
            "Примечание / замена"
        };
        var widths = new List<double> { 6, 50, 7, 8, 14, 15, 20, 8, 8, 24, 30 };
        if (withMoney)
        {
            headers.AddRange(new[] { "Себест. ед.", "Цена клиенту", "Маржа" });
            widths.AddRange(new[] { 13d, 13d, 13d });
        }
        var last = headers.Count;
        for (var c = 1; c <= last; c++)
            ws.Column(c).Width = widths[c - 1];
        ws.ShowGridLines = false;
        ws.Cell(1, 1).Value = snapshot.Document.ObjectName + " — контроль оборудования";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = snapshot.Document.Title + " · на " +
            DateTime.Today.ToString("dd.MM.yyyy", Ru);
        var rows = snapshot.Rows.Where(x => !x.Work).ToArray();
        var none = rows.Count(x => x.Status == "none");
        var issued = rows.Count(x => x.Status == "issued");
        var work = rows.Count(x => x.Status is "ordered" or "invoice" or "paid" or "shop_queued" or "shop_work");
        var stock = rows.Count(x => x.Status is "stock" or "part_stock");
        var summary = $"Позиций: {rows.Count(x => x.Status != "zero")} · не заказано: {none} · " +
                      $"в работе: {work} · на складе: {stock} · выдано: {issued}";
        if (withMoney)
        {
            var cost = rows.Where(x => !x.OutOfKp).Sum(x => (x.CostPrice ?? 0m) * x.Qty);
            var margin = rows.Where(x => !x.OutOfKp && x.CostPrice is not null)
                .Sum(x => (x.Price - x.CostPrice!.Value) * x.Qty);
            summary += $" · себестоимость: {cost:N2} · маржа: {margin:N2}";
        }
        ws.Cell(3, 1).Value = summary;
        ws.Cell(3, 1).Style.Font.FontSize = 10;
        for (var c = 1; c <= last; c++)
        {
            var cell = ws.Cell(5, c);
            cell.Value = headers[c - 1];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#E2E8F0");
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Alignment.WrapText = true;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        ws.Row(5).Height = 30;
        var line = 6;
        var positionNumber = 1;
        var systems = rows.GroupBy(x => x.SystemId)
            .Select(g => (System: g.Key, Name: g.First().SystemName, Rows: g.ToArray()));
        foreach (var system in systems)
        {
            if (system.Name != "Без системы")
            {
                ws.Cell(line, 1).Value = positionNumber++;
                ws.Cell(line, 2).Value = system.Name;
                ws.Cell(line, 2).Style.Font.Bold = true;
                for (var c = 1; c <= last; c++)
                {
                    ws.Cell(line, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#EEF2F7");
                    ws.Cell(line, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                }
                line++;
            }
            foreach (var row in system.Rows)
            {
                var stage = SqlServerProcurement.StatusTable.FirstOrDefault(x => x.Code == row.Status);
                // Never run user-origin text through FormulaA1.
                var label = row.Name + (row.OutOfKp ? " [вне КП]" : "");
                if (!row.OutOfKp) ws.Cell(line, 1).Value = positionNumber++;
                ws.Cell(line, 2).Value = label;
                ws.Cell(line, 3).Value = row.Unit;
                ws.Cell(line, 4).Value = row.Qty;
                ws.Cell(line, 5).Value = row.Supplier ?? "";
                ws.Cell(line, 6).Value = row.StatusLabel;
                ws.Cell(line, 6).Style.Font.Bold = true;
                ws.Cell(line, 7).Value = string.Join(" / ", new[]
                {
                    row.OrderRef,
                    row.InvoiceRef.Length > 0 ? "сч. " + row.InvoiceRef : ""
                }.Where(x => x.Length > 0)) +
                (row.DueDate is not null && row.Status is not ("issued" or "stock")
                    ? (row.DueDate.Value.Date < DateTime.Today ? " · ПРОСРОЧЕНО к " : " · ждём к ") +
                      row.DueDate.Value.ToString("dd.MM.yy", Ru)
                    : "");
                if (row.Received != 0m) ws.Cell(line, 8).Value = row.Received;
                if (row.Issued != 0m) ws.Cell(line, 9).Value = row.Issued;
                ws.Cell(line, 10).Value = snapshot.IssueDates.GetValueOrDefault(row.PositionId, "");
                ws.Cell(line, 11).Value =
                    (row.Replacement.Length > 0 ? "замена: " + row.Replacement : "") +
                    (row.Note.Length > 0 ? (row.Replacement.Length > 0 ? "; " : "") + row.Note : "");
                if (withMoney)
                {
                    if (row.CostPrice is not null) ws.Cell(line, 12).Value = row.CostPrice.Value;
                    if (!row.OutOfKp)
                    {
                        ws.Cell(line, 13).Value = row.Price;
                        if (row.CostPrice is not null)
                            ws.Cell(line, 14).FormulaA1 = $"(M{line}-L{line})*D{line}";
                    }
                }
                for (var c = 1; c <= last; c++)
                {
                    var cell = ws.Cell(line, c);
                    cell.Style.Fill.BackgroundColor =
                        XLColor.FromHtml(row.StatusBg.StartsWith('#') ? row.StatusBg : "#FFFFFF");
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    cell.Style.Alignment.WrapText = c is 2 or 10 or 11;
                    if (c is 4 or 8 or 9)
                        cell.Style.NumberFormat.Format = "0.######";
                    if (c >= 12)
                    {
                        cell.Style.NumberFormat.Format = "#,##0.00";
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    }
                }
                line++;
            }
        }
        ws.SheetView.FreezeRows(5);
        ws.SheetView.FreezeColumns(2);
        ws.Range(5, 1, Math.Max(5, line - 1), last).SetAutoFilter();
        ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.PrintAreas.Add($"A1:{(char)('A' + last - 1)}{Math.Max(line - 1, 5)}");
        var status = book.Worksheets.Add("Статусы");
        status.Column(1).Width = 22;
        var i = 1;
        foreach (var entry in SqlServerProcurement.StatusTable)
        {
            var cell = status.Cell(i++, 1);
            cell.Value = entry.Label;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(entry.Bg);
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = XLColor.FromHtml(entry.Fg);
        }
        return book;
    }
}
