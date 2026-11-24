using System.IO;
using ClosedXML.Excel;
using GmcSmeta.Core.Db;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Complete XLSX warehouse report with numeric cells and paginated journal retrieval.</summary>
internal static class MetalWarehouseReport
{
    public static void Export(SqlServerMetalWarehouse source, MetalSnapshot snapshot, string path)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireSameRevision(source, snapshot);
        using var book = new XLWorkbook();

        void Sheet(string title, string[] headers, IEnumerable<object?[]> rows, int[] numeric)
        {
            var ws = book.Worksheets.Add(title);
            for (var col = 0; col < headers.Length; col++)
                ws.Cell(1,col+1).Value = headers[col];
            var row = 2;
            foreach (var values in rows)
            {
                for (var col = 0; col < headers.Length; col++)
                {
                    var value = col < values.Length ? values[col] : null;
                    var cell = ws.Cell(row,col+1);
                    switch (value)
                    {
                        case decimal d: cell.Value=(double)d; break;
                        case double d: cell.Value=d; break;
                        case long l: cell.Value=(double)l; break;
                        case int i: cell.Value=i; break;
                        case DateTime dt: cell.Value=dt;cell.Style.DateFormat.Format="dd.MM.yyyy HH:mm";break;
                        case bool b: cell.Value=b?"Да":"Нет";break;
                        default: cell.Value= SafeText(value?.ToString()??""); break;
                    }
                }
                row++;
            }
            var last = Math.Max(1,row-1);
            var header = ws.Range(1,1,1,headers.Length);
            header.Style.Font.Bold=true;
            header.Style.Fill.BackgroundColor=XLColor.FromHtml("#E7EFF8");
            header.Style.Alignment.WrapText=true;
            header.Style.Alignment.Vertical=XLAlignmentVerticalValues.Center;
            ws.Row(1).Height=35;
            foreach (var col in numeric)
                if (last >= 2 && col > 0 && col <= headers.Length)
                    ws.Range(2,col,last,col).Style.NumberFormat.Format="#,##0.000";
            ws.SheetView.FreezeRows(1);
            ws.Range(1,1,last,headers.Length).SetAutoFilter();
            ws.Columns(1,headers.Length).AdjustToContents();
            for (var col=1;col<=headers.Length;col++)
                ws.Column(col).Width=Math.Clamp(ws.Column(col).Width+2,13,55);
        }

        static object?[] StockRow(MetalStockView x) =>
            [x.Category,x.Material,Area(x.Ours),x.Category=="Бухты"?x.OursPieces:null,
             Area(x.Supplier),x.Category=="Бухты"?x.SupplierPieces:null,
             Area(x.RemainingDemand),Area(x.Reserved),Area(x.Available),
             Area(x.Surplus),Area(x.Deficit)];
        Sheet("Остатки",
            ["Категория","Материал","У нас, м²","У нас, шт.","У поставщиков, м²",
             "У поставщиков, шт.","Ещё нужно, м²","В резерве, м²",
             "Можно выдать, м²","Излишек, м²","Дефицит, м²"],
            snapshot.Stock.Select(StockRow),[3,5,7,8,9,10,11]);
        Sheet("По объектам",
            ["Объект","Категория","Материал","Потребность, м²","Резерв, м²","Выдано, м²","Осталось, м²"],
            snapshot.Objects.Select(x => (object?[])[x.ObjectName,x.Category,x.Material,Area(x.Demand),
                Area(x.Reserved),Area(x.Issued),Area(x.Remaining)]),[4,5,6,7]);

        var movements = new List<MetalMovement>();
        for(var page=0;page<100000;page++)
        {
            var batch=source.ReadJournal(page,1000);
            movements.AddRange(batch);
            if(batch.Count<1000) break;
            if(page==99999) throw new InvalidOperationException("Слишком большой журнал.");
        }
        Sheet("Движения",
            ["Дата","Операция","Категория","Материал","м²","Шт.","Откуда","Куда",
             "Объект","Сотрудник","Поставщик","Счёт","Примечание"],
            movements.Select(x => (object?[])[x.CreatedUtc,x.Kind,x.Category,x.Material,
                Area(x.Kind is "Резерв" or "Снять резерв" ? Math.Abs(x.Reservation) : x.Area),
                x.Pieces,x.Source,x.Destination,x.ObjectName,x.User,x.Supplier,x.Invoice,x.Note]),[5]);
        Sheet("Выдано",
            ["Дата","Объект","Материал","Категория","Операция","Площадь, м²","Шт.","Примечание"],
            movements.Where(x => x.Kind is "Выдача" or "Возврат").Select(x => (object?[])
                [x.CreatedUtc,x.ObjectName,x.Material,x.Category,x.Kind,
                 Area(x.Kind=="Возврат"?-x.Area:x.Area),
                 x.Kind=="Возврат"?-x.Pieces:x.Pieces,x.Note]),[6]);
        Sheet("Резервы",
            ["Объект","Категория","Материал","Зарезервировано, м²"],
            snapshot.Objects.Where(x => x.Reserved!=0).Select(x => (object?[])
                [x.ObjectName,x.Category,x.Material,Area(x.Reserved)]),[4]);
        Sheet("Партии",
            ["Поставщик","Счёт","Материал","Заказано, м²","Бухты, шт.",
             "Оплачено","Получено, м²","Получено, шт.","У поставщика, м²","У поставщика, шт."],
            snapshot.Lots.Select(x => (object?[])
                [x.Supplier,x.Invoice,x.Material,Area(x.Ordered),x.OrderedPieces,x.Paid,
                 Area(x.Received),x.ReceivedPieces,Area(x.AtSupplier),x.PiecesAtSupplier]),
            [4,7,9]);
        Sheet("Статистика",["Показатель","Значение","Ед."],
            new object?[][]
            {
                new object?[] {"Материалов",snapshot.Stock.Count,"шт."},
                new object?[] {"Запас физически у нас",Area(snapshot.Stock.Sum(x=>x.Ours)),"м²"},
                new object?[] {"Оплачено, на хранении у поставщиков",Area(snapshot.Stock.Sum(x=>x.Supplier)),"м²"},
                new object?[] {"Осталось обеспечить",Area(snapshot.Stock.Sum(x=>x.RemainingDemand)),"м²"},
                new object?[] {"В резерве",Area(snapshot.Stock.Sum(x=>x.Reserved)),"м²"},
                new object?[] {"Дефицит",Area(snapshot.Stock.Sum(x=>x.Deficit)),"м²"},
                new object?[] {"Объектов с потребностями",snapshot.Objects.Select(x=>x.ObjectId is long id ? "id:"+id :
                    "name:"+x.ObjectName.Trim().ToUpperInvariant()).Distinct().Count(),"шт."},
                new object?[] {"Партии",snapshot.Lots.Count,"шт."},
                new object?[] {"Движений всего",movements.Count,"шт."}
            },[2]);
        RequireSameRevision(source, snapshot);
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!,
            ".gmc-metal-" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            book.SaveAs(temporary);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch { /* preserve the original error */ }
        }
    }

    private static void RequireSameRevision(SqlServerMetalWarehouse source, MetalSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Revision) || source.Revision() != snapshot.Revision)
            throw new InvalidOperationException("Склад изменился. Обновите данные и повторите экспорт.");
    }

    private static decimal Area(long milli) => MetalWarehouseRules.Sqm(milli);

    // Excel does not execute dangerous formula strings from user-entered object names,
    // invoices and notes; explicit textual escaping also helps other spreadsheet viewers.
    private static string SafeText(string text) =>
        text.Length>0 && "=+-@\t\r".Contains(text[0]) ? "'" + text : text;
}
