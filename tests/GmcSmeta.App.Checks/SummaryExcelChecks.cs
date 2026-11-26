using ClosedXML.Excel;
using GmcSmeta.App;
using Row = GmcSmeta.App.EstimateView.SummaryRow;

internal static class SummaryExcelChecks
{
    internal static void Run()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Сводная");
        SummaryDisplayedExcel.Fill(sheet, new[]
        {
            new Row { Kind = "system", Name = "Система", QtyText = "2 поз." },
            new Row { Name = "=1+2", Unit = "шт", QtyText = "1,25",
                CostUnitText = "1\u00a0234,50", PriceUnitText = "—",
                MCostText = "1\u202f543,12", WorkSellText = "0,00",
                MatMarginText = "−15,00", MarkupText = "50,00" },
            new Row { Name = "Вторая", QtyText = "2", WorkCostText = "" },
            new Row { Kind = "total", Name = "ИТОГО", TotalCostText = "1 543,12" },
        });
        using var stream = new System.IO.MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        using var reopened = new XLWorkbook(stream);
        var saved = reopened.Worksheet(1);
        Require(saved.Cell("A3").DataType == XLDataType.Text && !saved.Cell("A3").HasFormula &&
            saved.Cell("A3").GetString() == "=1+2", "Names must remain literal.");
        Require(saved.Cell("C2").GetString() == "2 поз.", "Group label lost.");
        Require(saved.Cell("C3").DataType == XLDataType.Number && saved.Cell("C3").GetDouble() == 1.25, "Quantity not numeric.");
        Require(saved.Cell("C4").Style.NumberFormat.Format == "#,##0", "Whole quantity separator.");
        Require(saved.Cell("D3").DataType == XLDataType.Number && saved.Cell("D3").GetDouble() == 1234.5, "Money not numeric.");
        Require(saved.Cell("E3").GetString() == "—", "Missing marker lost.");
        Require(saved.Cell("F3").GetDouble() == 1543.12, "Narrow space parsing.");
        Require(saved.Cell("I3").DataType == XLDataType.Number && saved.Cell("I3").GetDouble() == 0, "Zero lost.");
        Require(saved.Cell("H4").IsEmpty(), "Missing value must remain blank.");
        Require(saved.Cell("N3").GetDouble() == -15 && saved.Cell("P3").GetDouble() == 50, "Margin/markup.");
        Require(saved.Cell("L5").GetDouble() == 1543.12 && saved.Cell("A5").Style.Font.Bold, "Total or style lost.");
        Console.WriteLine("Summary displayed XLSX numeric cells and missing markers OK.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
