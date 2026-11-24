using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>Python pto/vor.py: explicit XLSX sheet/row/column mapping, no guessed values.</summary>
public static class PtoVor
{
    public sealed record Columns(int Name,int Qty,int Unit=0,int System=0);
    public sealed record Result(IReadOnlyList<PtoWork> Rows,IReadOnlyList<string> Warnings);

    public static IReadOnlyList<string> Sheets(string path)
    {
        using var book=new XLWorkbook(path);
        return book.Worksheets.Select(s=>s.Name).ToArray();
    }

    public static Result Read(string path,string sheet,int firstRow,Columns columns)
    {
        if(firstRow<1 || columns.Name<1 || columns.Qty<1)
            throw new ArgumentException("Укажите первую строку, колонку наименования и объёма.");
        var chosen=new[]{columns.Name,columns.Qty,columns.Unit,columns.System}
            .Where(v=>v>0).ToArray();
        if(chosen.Distinct().Count()!=chosen.Length)
            throw new ArgumentException("Выберите разные колонки для разных полей.");
        using var book=new XLWorkbook(path);
        if(!book.Worksheets.Contains(sheet))throw new ArgumentException("Лист не найден.");
        var ws=book.Worksheet(sheet);
        var lastRow=ws.LastRowUsed()?.RowNumber()??0;
        var lastColumn=ws.LastColumnUsed()?.ColumnNumber()??0;
        if(lastRow>50000 || lastColumn>500)
            throw new ArgumentException("Большая ВОР: сохраните нужный диапазон в отдельный XLSX.");
        if(firstRow>lastRow || chosen.Max()>lastColumn)
            throw new ArgumentException("Первая строка или колонка находится вне таблицы.");
        var warnings=new List<string>();
        var rows=new List<PtoWork>();
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for(var index=firstRow;index<=lastRow;index++)
        {
            var noCache=false;
            string Value(int column)
            {
                if(column<=0)return "";
                var cell=ws.Cell(index,column);
                if(cell.HasFormula)
                {
                    // Never calculate untrusted formulas or synthesize their result.
                    var cached=cell.CachedValue;
                    if(cached.IsBlank)
                    {
                        noCache=true;
                        return "";
                    }
                    return cached.ToString();
                }
                return cell.GetString();
            }
            var name=Value(columns.Name).Trim();
            var qty=Value(columns.Qty).Trim();
            var unit=Value(columns.Unit).Trim();
            var system=Value(columns.System).Trim();
            if(noCache)
            {
                warnings.Add($"Строка {index}: формула без сохранённого результата. Пересчитайте в Excel.");
                continue;
            }
            if(name.Length==0&&qty.Length==0)continue;
            if(name.Length==0||qty.Length==0)
            {
                warnings.Add($"Строка {index}: отсутствует наименование или объём. Строка пропущена.");
                continue;
            }
            var numeric=qty.Replace(" ","").Replace("\u00A0","").Replace(',','.');
            if(!decimal.TryParse(numeric,NumberStyles.Float,CultureInfo.InvariantCulture,out var amount)
                || amount<0)
            {
                warnings.Add($"Строка {index}: объём не является конечным неотрицательным числом — «{qty}». Строка пропущена.");
                continue;
            }
            if(unit.Length==0)
                warnings.Add($"Строка {index}: единица измерения не указана, проверьте вручную.");
            var key=system+"\0"+name+"\0"+unit+"\0"+numeric;
            if(!seen.Add(key))
            {
                warnings.Add($"Строка {index}: дубль исключён — проверьте исходную ВОР.");
                continue;
            }
            rows.Add(new PtoWork
            {
                System=system,Name=name,Unit=unit,Qty=qty,
                SourceFile=Path.GetFileName(path),SourceSheet=sheet,SourceRow=index
            });
        }
        return new Result(rows,warnings);
    }
}
