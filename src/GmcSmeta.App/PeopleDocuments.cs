using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using ClosedXML.Excel;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.App;

/// <summary>
/// Actual XLSX and DOCX operations; files are created before the immutable SQL history
/// is registered. No export succeeds with a missing or unreadable template.
/// </summary>
public static class PeopleDocuments
{
    // The letterhead's official requisites copied from Python people.py (not synthetic data).
    public const string GmcRequisites =
        "ИНН 2537101261 КПП 253601001 Юр.адрес: 690911, Приморский край, г. Владивосток, " +
        "ул. Адмирала Горшкова, д. 85, кв. 28 Р/С: 40702810450000009433 Банк: АО «Дальневосточный банк»\n" +
        "К/С: 30101810900000000705 БИК  040507705";

    public static string TemplatesFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GMC-Smeta-Next", "Шаблоны приказов");

    public static IReadOnlyList<string> Templates()
    {
        Directory.CreateDirectory(TemplatesFolder);
        var assembly = typeof(PeopleDocuments).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(x =>
            x.StartsWith("GmcSmeta.App.OrderTemplates.", StringComparison.Ordinal) &&
            x.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)))
        {
            var file = Path.Combine(TemplatesFolder, name["GmcSmeta.App.OrderTemplates.".Length..]);
            if (File.Exists(file)) continue; // Never overwrite user-edited Word templates.
            using var source = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidDataException("Не найден встроенный Word-шаблон.");
            using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            source.CopyTo(output);
        }
        return Directory.EnumerateFiles(TemplatesFolder, "*.docx")
            .Where(x => !Path.GetFileName(x).StartsWith("~$", StringComparison.Ordinal))
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
    public static string TemplateTitle(string path) =>
        Regex.Replace(Path.GetFileNameWithoutExtension(path), @"^\d+[\s._-]*", "").Trim();

    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return "";
        if (cell.DataType == XLDataType.DateTime)
            return cell.GetDateTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        return PeopleRules.Clean(cell.GetFormattedString());
    }

    public static PeopleImportPreview ReadImport(string path,
        int? fioColumn = null, int? postColumn = null, int? birthColumn = null,
        int? modelColumn = null, int? plateColumn = null)
    {
        using var book = new XLWorkbook(path);
        var people = new List<PeopleImportRow>();
        var cars = new List<CarImportRow>();
        foreach (var sheet in book.Worksheets)
        {
            var area = sheet.RangeUsed();
            if (area is null) continue;
            // Manual column mapping must also work without a recognizable header row.
            string mode = fioColumn.HasValue ? "people" :
                modelColumn.HasValue || plateColumn.HasValue ? "cars" : "";
            int fio = fioColumn ?? 0, post = postColumn ?? 0, birth = birthColumn ?? 0;
            int model = modelColumn ?? 0, plate = plateColumn ?? 0;
            for (int n = area.FirstRow().RowNumber(); n <= area.LastRow().RowNumber(); n++)
            {
                var row = sheet.Row(n);
                var cells = Enumerable.Range(1, Math.Min(30, area.LastColumn().ColumnNumber()))
                    .Select(i => CellText(row.Cell(i))).ToArray();
                var headers = cells.Select(x => x.ToLowerInvariant()).ToArray();
                var fioHeader = Array.FindIndex(headers, x =>
                    x.StartsWith("фио") || x.StartsWith("ф.и.о"));
                if (fioHeader >= 0)
                {
                    mode = "people";
                    if (fioColumn is null) fio = fioHeader + 1;
                    if (postColumn is null)
                        post = Array.FindIndex(headers, x => x.StartsWith("должн")) + 1;
                    if (birthColumn is null)
                        birth = Array.FindIndex(headers, x => x.Contains("рожд")) + 1;
                    continue;
                }
                if (headers.Any(x => x.StartsWith("автомоб") || x.StartsWith("транспорт") ||
                    x.StartsWith("техника")))
                {
                    mode = "cars";
                    continue;
                }
                var plateHeader = Array.FindIndex(headers, x => x.Contains("госномер") ||
                    x.Contains("гос.номер") || x.Contains("регистрационный номер"));
                var modelHeader = Array.FindIndex(headers, x => x.StartsWith("марка") ||
                    x.StartsWith("модель") || x.StartsWith("марка/модель"));
                if (plateHeader >= 0 && modelHeader >= 0)
                {
                    mode = "cars";
                    if (plateColumn is null) plate = plateHeader + 1;
                    if (modelColumn is null) model = modelHeader + 1;
                    continue;
                }
                if (headers.All(string.IsNullOrWhiteSpace)) continue;
                if (mode == "people" && fio > 0)
                {
                    var name = fio <= cells.Length ? cells[fio - 1] : "";
                    if (name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2 ||
                        name.Any(char.IsDigit)) continue;
                    var p = post > 0 && post <= cells.Length ? cells[post - 1] : "";
                    var b = birth > 0 && birth <= cells.Length ? cells[birth - 1] : "";
                    people.Add(new PeopleImportRow(name, p, PeopleRules.BirthNorm(b)));
                }
                else if (mode == "cars")
                {
                    // Legacy access lists have model in column B, plate in D.
                    var values = cells.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                    var carPlate = plate > 0 && plate <= cells.Length ? cells[plate - 1] :
                        values.Reverse().FirstOrDefault(x => Regex.IsMatch(x, @"\d{3}") && x.Length <= 12) ?? "";
                    var carModel = model > 0 && model <= cells.Length ? cells[model - 1] :
                        values.FirstOrDefault(x => x != carPlate && !int.TryParse(x, out _)) ?? "";
                    // A car row always has at least the ordinal number plus model/plate;
                    // merged trailing rows (signer, totals) have a single value and are not cars.
                    if ((carModel.Length > 0 || carPlate.Length > 0) && values.Length >= 2)
                        cars.Add(new CarImportRow(carModel, carPlate));
                }
            }
        }
        return new PeopleImportPreview(people, cars);
    }

    private static void Merge(IXLWorksheet ws, int row, string text, double size, bool bold = false,
        double? height = null, bool wrap = false, XLAlignmentHorizontalValues alignment = XLAlignmentHorizontalValues.Center,
        bool borderBottom = false)
    {
        var region = ws.Range(row, 1, row, 4);
        region.Merge();
        var c = ws.Cell(row, 1);
        c.Value = text;
        c.Style.Font.FontName = "Times New Roman";
        c.Style.Font.FontSize = size;
        c.Style.Font.Bold = bold;
        c.Style.Alignment.Horizontal = alignment;
        c.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        c.Style.Alignment.WrapText = wrap;
        if (borderBottom) region.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        if (height.HasValue) ws.Row(row).Height = height.Value;
    }
    public static void ExportAccessList(string path, AccessCompany? company, string requisites,
        string intro, IReadOnlyList<AccessPerson> people, IReadOnlyList<AccessCar> cars, string signer)
    {
        if (people.Count == 0) throw new ArgumentException("Выберите хотя бы одного сотрудника.");
        using var book = new XLWorkbook();
        var ws = book.Worksheets.Add("Список");
        foreach (var (col, width) in new[] { (1, 7.7), (2, 38d), (3, 30d), (4, 17.7) })
            ws.Column(col).Width = width;
        var name = company?.Name ?? "";
        var full = "";
        var shortName = name.Trim();
        foreach (var (abbr, expanded) in new[] { ("ООО", "Общество с ограниченной ответственностью"),
            ("АО", "Акционерное общество"), ("ИП", "Индивидуальный предприниматель") })
            if (shortName.StartsWith(abbr + " ", StringComparison.OrdinalIgnoreCase))
            {
                full = expanded;
                shortName = shortName[(abbr.Length + 1)..].Trim();
                break;
            }
        Merge(ws, 2, full, 16, true, 20.25);
        Merge(ws, 3, shortName, 20, true, 25.5, borderBottom: string.IsNullOrWhiteSpace(requisites));
        if (!string.IsNullOrWhiteSpace(requisites))
            Merge(ws, 4, requisites, 10, true, 40.5, true, borderBottom: true);
        Merge(ws, 6, "   " + intro.Trim(), 12, true,
            Math.Max(33, 16.5 * (intro.Length / 80 + 1)), true, XLAlignmentHorizontalValues.Left);
        Merge(ws, 7, "Прошу разрешить допуск ниже перечисленным сотрудникам" +
            (cars.Count > 0 ? " и автомобилям:" : ":"), 12, true,
            alignment: XLAlignmentHorizontalValues.Left);
        var row = 9;
        for (int i = 1; i <= 4; i++)
        {
            var cell = ws.Cell(row, i);
            cell.Value = new[] { "№", "ФИО", "должность", "дата рождения" }[i - 1];
            StyleCell(cell, 12, centered: true);
        }
        for (int n = 0; n < people.Count; n++)
        {
            row++;
            var p = people[n];
            ws.Cell(row, 1).Value = n + 1;
            ws.Cell(row, 2).Value = p.Fio;
            ws.Cell(row, 3).Value = p.Post;
            if (DateTime.TryParseExact(PeopleRules.BirthNorm(p.Birth), "dd.MM.yyyy",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                ws.Cell(row, 4).Value = date;
                ws.Cell(row, 4).Style.DateFormat.Format = "dd.MM.yyyy";
            }
            else ws.Cell(row, 4).Value = p.Birth;
            for (var i = 1; i <= 4; i++) StyleCell(ws.Cell(row, i), 10, i is 1 or 4);
        }
        if (cars.Count > 0)
        {
            row += 2;
            ws.Cell(row, 2).Value = "Автомобили";
            ws.Cell(row, 2).Style.Font.FontName = "Times New Roman";
            ws.Cell(row, 2).Style.Font.FontSize = 12;
            for (int n = 0; n < cars.Count; n++)
            {
                row++;
                ws.Cell(row, 1).Value = n + 1;
                ws.Cell(row, 2).Value = cars[n].Model;
                ws.Cell(row, 4).Value = cars[n].Plate;
                for (int i = 1; i <= 4; i++) StyleCell(ws.Cell(row, i), 10, i != 2);
            }
        }
        row += 4;
        Merge(ws, row, signer, 12, alignment: XLAlignmentHorizontalValues.Left);
        // The signature already spans four columns above; do not merge an overlapping range.
        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.Margins.Left = 0.6;
        ws.PageSetup.Margins.Right = 0.6;
        ws.PageSetup.PrintAreas.Add("A1:D" + (row + 1));
        // Direct CreateNew prevents accidental overwrite of a list already issued.
        // A failed write is rolled back at the filesystem level before SQL registration.
        var created = false;
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            book.SaveAs(output);
            output.Flush(true);
        }
        catch
        {
            if (created)
            {
                try { File.Delete(path); } catch (IOException) { /* do not hide the original error */ }
            }
            throw;
        }
    }
    private static void StyleCell(IXLCell cell, double size, bool centered)
    {
        cell.Style.Font.FontName = "Times New Roman";
        cell.Style.Font.FontSize = size;
        cell.Style.Alignment.Horizontal = centered ?
            XLAlignmentHorizontalValues.Center : XLAlignmentHorizontalValues.Left;
        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        cell.Style.Alignment.WrapText = true;
        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
    }

    private static readonly Regex Paragraphs = new(@"<w:p(?:\s[^>]*)?>.*?</w:p>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TextNodes = new(@"(<w:t(?:\s[^>]*)?>)([^<]*)(</w:t>)",
        RegexOptions.Compiled);
    public static string ReplaceWordXml(string xml, IReadOnlyDictionary<string, string> mapping)
    {
        return Paragraphs.Replace(xml, para =>
        {
            var nodes = TextNodes.Matches(para.Value).Cast<Match>().ToArray();
            if (nodes.Length == 0) return para.Value;
            var texts = nodes.Select(n => System.Net.WebUtility.HtmlDecode(n.Groups[2].Value)).ToArray();
            var joined = string.Concat(texts);
            if (!mapping.Keys.Any(joined.Contains)) return para.Value;
            foreach (var pair in mapping.OrderByDescending(x => x.Key.Length))
            {
                var start = 0;
                while (true)
                {
                    joined = string.Concat(texts);
                    var i = joined.IndexOf(pair.Key, start, StringComparison.Ordinal);
                    if (i < 0) break;
                    var end = i + pair.Key.Length;
                    var offset = 0;
                    var first = true;
                    for (int n = 0; n < texts.Length; n++)
                    {
                        var text = texts[n];
                        var a = offset;
                        var b = offset += text.Length;
                        if (b <= i || a >= end) continue;
                        var lo = Math.Max(i, a) - a;
                        var hi = Math.Min(end, b) - a;
                        texts[n] = text[..lo] + (first ? pair.Value : "") + text[hi..];
                        first = false;
                    }
                    start = i + pair.Value.Length;
                }
            }
            var result = new StringBuilder();
            var prev = 0;
            for (int n = 0; n < nodes.Length; n++)
            {
                var node = nodes[n];
                result.Append(para.Value, prev, node.Index - prev);
                var tag = node.Groups[1].Value;
                if (!tag.Contains("xml:space=", StringComparison.Ordinal))
                    tag = tag[..^1] + " xml:space=\"preserve\">";
                result.Append(tag);
                result.Append(SecurityElement.Escape(texts[n]) ?? "");
                result.Append(node.Groups[3].Value);
                prev = node.Index + node.Length;
            }
            return result.Append(para.Value, prev, para.Value.Length - prev).ToString();
        });
    }
    public static IReadOnlyDictionary<string, string> OrderMapping(AccessSite site, PeoplePerson person,
        AccessCompany? company, DateTime day, string number, string contract, string siteName)
    {
        var post = PeopleRules.Clean(person.Post);
        var date = PeopleRules.DateLong(day);
        return new Dictionary<string, string>
        {
            ["{Должность}" + new string(' ', 30)] = (post.Length > 0 ?
                char.ToUpperInvariant(post[0]) + post[1..] : "") +
                new string(' ', Math.Max(2, 36 - post.Length)),
            ["{ДАТА}" + new string(' ', 40)] = date +
                new string(' ', Math.Max(2, 36 - 2 * Math.Max(0, date.Length - 18))),
            ["{ДАТА_ЦИФРЫ}"] = day.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["{ДАТА}"] = date,
            ["{ГОД}"] = day.Year.ToString(CultureInfo.InvariantCulture),
            ["{НОМЕР}"] = number,
            ["{ОБЪЕКТ}"] = PeopleRules.Clean(siteName).Trim('«','»','"',' '),
            ["{ДОГОВОР}"] = contract,
            ["{ЗАКАЗЧИК}"] = site.Customer,
            ["{ФИО_КОГО}"] = person.DisplayGenitive,
            ["{ДОЛЖНОСТЬ_КОГО}"] = person.DisplayPostGenitive,
            ["{Должность}"] = post.Length > 0 ? char.ToUpperInvariant(post[0]) + post[1..] : "",
            ["{ФИО_КРАТКО}"] = PeopleRules.ShortFio(person.Fio),
            ["{ФИО}"] = person.Fio,
            ["{ДИРЕКТОР}"] = !string.IsNullOrWhiteSpace(company?.SignerName) ?
                company.SignerName : "Цурупий А.В."
        };
    }
    public static void FillDocx(string template, string path, IReadOnlyDictionary<string, string> mapping)
    {
        if (!File.Exists(template)) throw new FileNotFoundException("Отсутствует Word-шаблон.", template);
        using var source = ZipFile.OpenRead(template);
        if (source.GetEntry("word/document.xml") is null)
            throw new InvalidDataException("Шаблон не является DOCX-документом Word.");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        bool ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                var created = archive.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                created.LastWriteTime = entry.LastWriteTime;
                using var input = entry.Open();
                using var target = created.Open();
                if (entry.FullName.StartsWith("word/", StringComparison.OrdinalIgnoreCase) &&
                    entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                    (entry.FullName.Contains("document", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.Contains("header", StringComparison.OrdinalIgnoreCase) ||
                     entry.FullName.Contains("footer", StringComparison.OrdinalIgnoreCase)))
                {
                    using var read = new StreamReader(input, Encoding.UTF8);
                    var xml = ReplaceWordXml(read.ReadToEnd(), mapping);
                    if (Regex.IsMatch(xml, @"\{[А-Яа-я_]+\}"))
                        throw new InvalidDataException("В шаблоне есть неизвестные поля или незаполненные заполнители.");
                    using var write = new StreamWriter(target, new UTF8Encoding(false), leaveOpen: true);
                    write.Write(xml);
                    write.Flush();
                }
                else input.CopyTo(target);
            }
        }
                output.Flush(true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (ownsTemporary && File.Exists(temporary))
                try { File.Delete(temporary); } catch (IOException) { /* preserve original failure */ }
        }
    }
    public static string UniquePath(string folder, string stem, string ext)
    {
        Directory.CreateDirectory(folder);
        var safe = PeopleRules.SafeFileName(stem);
        if (safe.Length == 0) safe = "Документ";
        for (var i = 1; i < 10000; i++)
        {
            var path = Path.Combine(folder, safe + (i == 1 ? "" : " (" + i + ")") + ext);
            if (!File.Exists(path)) return path;
        }
        throw new IOException("Слишком много файлов с одним именем.");
    }
}
