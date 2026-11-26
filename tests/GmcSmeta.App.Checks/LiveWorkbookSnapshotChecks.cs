using System.IO;
using ClosedXML.Excel;
using GmcSmeta.App;
using GmcSmeta.Core;

internal static class LiveWorkbookSnapshotChecks
{
    internal static void Run()
    {
        var folder = Directory.CreateTempSubdirectory("gmc-live-export-").FullName;
        try
        {
            using var book = new XLWorkbook();
            var sheet = book.AddWorksheet("Металл");
            sheet.Cell("A1").Value = 15;
            sheet.Cell("B1").FormulaA1 = "A1*2";
            var path = Path.Combine(folder, "register.xlsx");
            ExportFilePublication.WriteBytes(path, LiveWorkbookSnapshot.Capture(book));
            File.Delete(path);
            // The live workbook must not depend on the exported file or
            // disposed staging stream; it remains editable after export.
            sheet.Cell("A1").Value = 30;
            var bytes = LiveWorkbookSnapshot.Capture(book);
            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reopened = new XLWorkbook(stream))
            {
                if (reopened.Worksheet(1).Cell("A1").GetDouble() != 30 ||
                    reopened.Worksheet(1).Cell("B1").FormulaA1 != "A1*2")
                    throw new Exception("Repeated live workbook snapshot lost edits or formulas.");
            }
            // A publication error must not poison the live editor.
            var blocked = Directory.CreateDirectory(path).FullName;
            try
            {
                ExportFilePublication.WriteBytes(blocked, LiveWorkbookSnapshot.Capture(book));
                throw new Exception("Unexpected export success.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            sheet.Cell("A1").Value = 45;
            using var lastStream = new MemoryStream(LiveWorkbookSnapshot.Capture(book));
            using var last = new XLWorkbook(lastStream);
            if (last.Worksheet(1).Cell("A1").GetDouble() != 45)
                throw new Exception("Live editor cannot save after failed export.");
            Console.WriteLine("Repeated live workbook snapshots and failed export recovery OK.");
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
