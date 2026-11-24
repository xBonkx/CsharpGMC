using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

internal sealed record MetalOpeningPreview(
    IReadOnlyList<MetalOpening> Records, IReadOnlyList<string> Warnings, byte[] Original, string FileName)
{
    public long TotalStock => Records.Sum(x => x.Stock.Sum(p => MetalWarehouseRules.Units(p.Area)));
    public long TotalDemand => Records.Sum(x => x.Demand.Sum(p => MetalWarehouseRules.Units(p.Area)));
}

internal static class MetalOpeningReader
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static MetalOpeningPreview Read(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() != ".xlsx")
            throw new InvalidDataException("Исходный учёт металла должен быть в формате .xlsx.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is 0 or > 5_000_000)
            throw new InvalidDataException("Excel-файл отсутствует, пуст или больше 5 МБ.");
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    public static MetalOpeningPreview Parse(byte[] bytes, string fileName)
    {
        if (bytes.Length == 0 || bytes.Length > 5_000_000) throw new InvalidDataException("Файл больше 5 МБ.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var book = new XLWorkbook(stream);
        var sheets = book.Worksheets.ToList();
        if (sheets.Count < 2)
            throw new InvalidDataException("Нужны первые два листа исходной книги учёта металла.");

        var sheet = sheets[0];
        var bounds = sheet.RangeUsed(XLCellsUsedOptions.Contents)?.RangeAddress;
        var rows = bounds?.LastAddress.RowNumber ?? 0;
        var cols = bounds?.LastAddress.ColumnNumber ?? 0;
        if (rows < 2 || cols > 250 || (long)rows * cols > 250_000)
            throw new InvalidDataException("Первый лист пустой или слишком большой.");
        if (!Text(sheet.Cell(2,1)).Equals("металл/объект", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("На первом листе не найден заголовок «Металл/объект».");

        var items = new List<MetalOpening>();
        var warnings = new List<string>();
        string category = "";
        int header = 0;
        var locations = Array.Empty<(int Column, string Place)>();
        for (var row = 1; row <= rows; row++)
        {
            var cell = sheet.Cell(row,1);
            var label = Text(cell);
            var section = label.ToLowerInvariant();
            if (section.StartsWith("огнезащита", StringComparison.Ordinal)) break;
            switch (section)
            {
                case "металл/объект":
                    category = "Бухты";
                    locations = [(2,"Станки"),(3,"В наличии"),(5,"У поставщиков")];
                    header = row; continue;
                case "металл в штрипсах":
                    category = "Штрипсы";
                    locations = [(4,"Цех Выселковая")];
                    header = row; continue;
                case "металл в листах":
                    category = "Листы";
                    locations = [(2,"Склад"),(3,"Цех Каплунова"),(4,"Цех Выселковая")];
                    header = row; continue;
            }
            if (category.Length == 0 || label.Length == 0) continue;
            if (!cell.CachedValue.IsNumber && !(category == "Штрипсы" && label.StartsWith("(")))
                continue;

            decimal Number(int column)
            {
                var v = sheet.Cell(row,column).CachedValue;
                if (v.IsBlank) return 0;
                if (!v.IsNumber) throw new InvalidDataException(
                    $"В ячейке {sheet.Cell(row,column).Address} ожидалось число. Проверьте файл в Excel.");
                return Convert.ToDecimal(v.GetNumber(), Invariant);
            }

            var stocks = locations.Select(p => (p.Place, Number(p.Column))).ToArray();
            IEnumerable<int> objectColumns = category switch
            {
                "Бухты" => Enumerable.Range(7,23).Concat([33,34]),
                "Штрипсы" => Enumerable.Range(7,23),
                _ => []
            };
            var demand = new List<(string Object, decimal Area)>();
            foreach (var column in objectColumns)
            {
                var qty = Number(column);
                if (qty == 0) continue;
                var objectName = Text(sheet.Cell(header,column));
                if (objectName.Length == 0)
                {
                    objectName = $"Не указан ({sheet.Cell(row,column).Address.ColumnLetter})";
                    warnings.Add($"{label}: есть потребность в столбце без названия объекта.");
                }
                demand.Add((objectName,qty));
            }
            if (category == "Штрипсы" && Number(5) != 0)
                warnings.Add($"{label}: ожидаемое поступление штрипс не входит в запас.");
            decimal? cached = null;
            if (category != "Листы" && sheet.Cell(row,30).CachedValue.IsNumber)
                cached = Convert.ToDecimal(sheet.Cell(row,30).CachedValue.GetNumber(), Invariant);
            var pieces = category == "Бухты"
                ? new[] { ("В наличии", Number(4)), ("У поставщиков", Number(6)) }
                : Array.Empty<(string, decimal)>();
            var item = new MetalOpening(category,label,stocks,demand,cached,pieces);
            var balance = stocks.Sum(x => x.Item2) - demand.Sum(x => x.Area);
            if (cached is decimal expected &&
                decimal.Round(balance,2) != decimal.Round(expected,2))
                warnings.Add($"{category} · {label}: рассчитано {balance:0.###} м²; Excel {expected:0.###} м².");
            items.Add(item);
        }
        MetalWarehouseRules.ValidateOpening(items);

        var history = sheets[1];
        var lastColumn = Math.Min(history.LastColumnUsed(XLCellsUsedOptions.Contents)?.ColumnNumber() ?? 0,250);
        for (var col = 1; col <= lastColumn; col++)
        {
            var value = Text(history.Cell(2,col));
            if (value.Contains(".2002 ", StringComparison.Ordinal))
                warnings.Add("Дата закупки требует сверки: " + value);
        }
        return new MetalOpeningPreview(items,warnings,bytes,fileName);
    }

    private static string Text(IXLCell cell)
    {
        var value = cell.CachedValue;
        if (value.IsBlank) return "";
        return (value.IsNumber
                ? value.GetNumber().ToString(Invariant)
                : value.ToString()).Trim();
    }
}
