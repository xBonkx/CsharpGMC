using System.Windows.Controls;
using ClosedXML.Excel;
using GmcSmeta.App;

internal static class MetalRegisterSheetChecks
{
    internal static void Run()
    {
        using var book = new XLWorkbook();
        var sheet = book.AddWorksheet("Лист1");
        sheet.Cell("A1").Value = "металл/объект";
        sheet.Cell("B1").Value = "Площадь";
        sheet.Cell("A2").Value = "Объект А";
        sheet.Cell("B2").Value = 15;
        sheet.Cell("D2").FormulaA1 = "B2*2";
        sheet.Cell("A3").Value = new DateTime(2026,1,1);
        sheet.Cell("E3").Value = "не включать строку даты";
        sheet.Cell("A5").Value = "металл в листах";
        sheet.Cell("A6").Value = "Объект Б";
        sheet.Cell("C6").Value = 20;
        var description = MetalRegisterSheet.Describe(sheet);
        if (description.Sections.Count != 2 || description.RowLimit != 6 || description.ColLimit != 5)
            throw new Exception("Metal sheet description lost dimensions/sections.");
        int edits = 0;
        var page = new MetalRegisterSheet(description, () => edits++);
        var dock = (DockPanel)page.Content;
        var grid = dock.Children.OfType<DataGrid>().Single();
        var picker = dock.Children.OfType<StackPanel>().Single().Children.OfType<ComboBox>().Single();
        if (grid.Items.Count != 1 || grid.Columns.Count != 3 || page.Read(2,"C2") != "30")
            throw new Exception("Section projection lost formula or included date/empty rows.");
        page.Write(2,"C1","25");
        if (page.Read(2,"C2") != "50" || edits != 1 || sheet.Cell("D2").FormulaA1 != "B2*2")
            throw new Exception("Metal formula cache not invalidated or original cell changed.");
        picker.SelectedIndex = 1;
        if (grid.Items.Count != 1 || grid.Columns.Count != 2 || page.Read(6,"C1") != "20")
            throw new Exception("Section switch changed source column mapping.");
        picker.SelectedIndex = 0;
        if (page.Read(2,"C1") != "25" || !page.CommitEditor())
            throw new Exception("Section switch lost edits.");
        Console.WriteLine("Metal register section projection and formula invalidation OK.");
    }
}
