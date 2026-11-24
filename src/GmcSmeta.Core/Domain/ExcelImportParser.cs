using System.Globalization;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Lossless Excel preview data. These are not database entities and have no side effects.</summary>
public sealed class ExcelImportPosition
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public decimal? PurchasePrice { get; set; }
    public decimal? WorkPrice { get; set; }
    public string Note { get; set; } = "";
    public int SourceRow { get; set; }
    public Dictionary<string, string> Extra { get; } = new();
    public List<ExcelImportPosition> Children { get; } = new();
}

public sealed class ExcelImportSection
{
    public string Name { get; set; } = "";
    public string Parent { get; set; } = "";
    public string Heading { get; set; } = "";
    public string SourceNumber { get; set; } = "";
    public decimal? Smr { get; set; }
    public List<ExcelImportPosition> Positions { get; } = new();
    public decimal UnroundedMaterialSum => Positions.Sum(p => p.Quantity * p.Price);
    public decimal MaterialSum => DecimalRound(UnroundedMaterialSum);
    internal static decimal DecimalRound(decimal n) => decimal.Round(n, 2, MidpointRounding.ToEven);
}

public sealed record ExcelImportCheck(bool Passed, string Description);

public sealed class ExcelImportBlock
{
    public string Sheet { get; set; } = "";
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "kp";
    public string Number { get; set; } = "";
    public string Date { get; set; } = "";
    public string Form { get; set; } = "";
    public bool VolumesOnly { get; set; }
    public bool HasPurchase { get; set; }
    public bool HasWork { get; set; }
    public decimal? DeclaredMaterial { get; set; }
    public decimal? DeclaredTotal { get; set; }
    public decimal? Transport { get; set; }
    public decimal? Smr { get; set; }
    public decimal? Pnr { get; set; }
    public decimal? VatDeclared { get; set; }
    public decimal VatRate { get; set; } = 22m;
    public int FirstRow { get; set; }
    public int LastRow { get; set; }
    public List<ExcelImportSection> Sections { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<ExcelImportCheck> Checks { get; } = new();
    public int PositionCount => Sections.Sum(s => s.Positions.Sum(p => 1 + p.Children.Count));
    // Python sums exact Excel products across all systems and rounds only once.
    public decimal MaterialSum => ExcelImportSection.DecimalRound(Sections.Sum(s => s.UnroundedMaterialSum));
    // DNS customer spreadsheets round EACH row to kopecks (half-up / away from zero).
    public decimal DnsRowsSum => Sections.Sum(s => s.Positions.Sum(p =>
        decimal.Round(p.Quantity * p.Price, 2, MidpointRounding.AwayFromZero)));
    public decimal WorkSum => ExcelImportSection.DecimalRound(Sections.Sum(s =>
        s.Positions.Sum(p => p.Quantity * (p.WorkPrice ?? 0m))));
    public decimal NetWithoutVat => ExcelImportSection.DecimalRound(
        (Form == "dns" ? DnsRowsSum : MaterialSum) +
        (HasWork ? WorkSum : Smr ?? 0m) + (Transport ?? 0m) + (Pnr ?? 0m));
    public decimal CalculatedTotal => Form == "dns"
        ? NetWithoutVat + ExcelImportSection.DecimalRound(NetWithoutVat * VatRate / 100m)
        : NetWithoutVat;
    public bool Ok => Checks.Count > 0 && Checks.All(c => c.Passed);
}

public sealed class ExcelImportResult
{
    public string FileName { get; init; } = "";
    public List<ExcelImportBlock> Blocks { get; } = new();
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Structured, SQL-independent port of smeta/importer.py. Call once for each worksheet.
/// Actual XLSX reading (including formula cache values) lives in the WPF adapter.
/// </summary>
public static partial class ExcelImportParser
{
    private static readonly Regex NumberRegex = new(@"^[+-]?(?:\d+(?:\.\d*)?|\.\d+)$", RegexOptions.Compiled);
    private static readonly Regex DateRegex = new(@"\b\d{1,2}\.\d{1,2}\.\d{2,4}\b", RegexOptions.Compiled);
    private static readonly Regex VatRegex = new(@"НДС\s*(\d{1,2}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SystemCode = new(@"^[A-Za-zА-Яа-яЁё]{1,4}\s?\d", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(@"^\s*(вариант|раздел|пункт|часть|этап|корпус|секция)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed class Header
    {
        public int? Number, Unit, Quantity, Note, Work, WorkTotal, Basis;
        public List<int> Names { get; } = new();
        public List<int> Prices { get; } = new();
        public List<int> Totals { get; } = new();
        public int Name => Names[^1];
        public int? Price => Prices.Count == 0 ? null : Prices[^1];
        public int? Total => Totals.Count == 0 ? null : Totals[^1];
        public int? PurchasePrice => Prices.Count > 1 ? Prices[0] : null;
        public HashSet<int> Used()
        {
            var set = new HashSet<int>(Names.Concat(Prices).Concat(Totals));
            foreach (var x in new[] { Number, Unit, Quantity, Note, Work, WorkTotal, Basis })
                if (x.HasValue) set.Add(x.Value);
            return set;
        }
    }

    public static ExcelImportResult ParseWorkbook(string filename,
        IEnumerable<(string Sheet, IReadOnlyList<IReadOnlyList<object?>> Rows)> sheets,
        CancellationToken cancellationToken = default)
    {
        var result = new ExcelImportResult { FileName = filename };
        foreach (var (name, rows) in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dns = TryParseDns(name, rows);
            if (dns is not null)
            {
                result.Blocks.Add(dns);
                continue;
            }
            var basic = ParseSheetCore(name, rows, false, cancellationToken);
            if (basic.Blocks.Count == 0 && TryParseRevit(name, rows) is { } revit)
                basic.Blocks.Add(revit);
            if (basic.Blocks.Count > 1 && CountHeaders(rows) == 1)
            {
                var joined = ParseSheetCore(name, rows, true, cancellationToken);
                var hasSectionTotals = rows.Any(r => r.Take(4).Any(v => Text(v).Contains("по раздел", StringComparison.OrdinalIgnoreCase)));
                if (joined.Blocks.Count < basic.Blocks.Count &&
                    joined.Blocks.All(b => b.DeclaredTotal is not null) &&
                    joined.Blocks.Sum(b => b.PositionCount) >= basic.Blocks.Sum(b => b.PositionCount) &&
                    (joined.Blocks.All(b => b.Ok) || hasSectionTotals))
                {
                    foreach (var block in joined.Blocks)
                        block.Warnings.Insert(0, "Промежуточные итоги систем объединены в один документ.");
                    basic = joined;
                }
            }
            foreach (var parsed in basic.Blocks.Where(b => !b.VolumesOnly))
                ApplyVatOnTop(parsed, rows);
            result.Blocks.AddRange(basic.Blocks);
            result.Notes.AddRange(basic.Notes.Select(n => $"Лист «{name}»: {n}"));
        }
        return result;
    }

    private static int CountHeaders(IReadOnlyList<IReadOnlyList<object?>> rows) => rows.Count(r => DetectHeader(r) is not null);

    private static (List<ExcelImportBlock> Blocks, List<string> Notes) ParseSheetCore(
        string sheet, IReadOnlyList<IReadOnlyList<object?>> rows, bool mergeTotals, CancellationToken ct)
    {
        var blocks = new List<ExcelImportBlock>();
        var notes = new List<string>();
        Header? columns = null;
        ExcelImportBlock? block = null;
        ExcelImportSection? system = null;
        ExcelImportPosition? lastTop = null;
        var captions = new List<string>();
        var headerMeta = new List<string>();
        var title = "";
        var rootSystem = "";
        var componentCaption = "";
        var skipRow = -1;
        var missingPrices = new List<int>();
        var subtotals = new Dictionary<ExcelImportBlock, List<(int Row, string Kind, decimal Value, string Label, ExcelImportSection? Section)>>();

        ExcelImportBlock NewBlock(int row)
        {
            var b = new ExcelImportBlock
            {
                Sheet = sheet, Title = title, FirstRow = row,
                HasPurchase = columns?.PurchasePrice is not null,
                HasWork = columns?.Work is not null
            };
            DetectMetadata(headerMeta, b);
            blocks.Add(b);
            block = b;
            system = null;
            lastTop = null;
            title = "";
            return b;
        }
        ExcelImportSection EnsureSystem()
        {
            if (system is null)
            {
                system = new ExcelImportSection { Name = "Без системы" };
                block!.Sections.Add(system);
            }
            return system;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if ((i & 63) == 0) ct.ThrowIfCancellationRequested();
            if (i == skipRow) continue;
            var row = rows[i];
            var rowNumber = i + 1;
            var h = DetectHeader(row);
            if (h is not null)
            {
                columns = h;
                if (i + 1 < rows.Count && RefineHeader(h, row, rows[i + 1])) skipRow = i + 1;
                title = captions.LastOrDefault(t => TitleRegex.IsMatch(t)) ?? "";
                headerMeta = captions.ToList();
                if (block is not null && block.PositionCount > 0 && block.DeclaredTotal is null)
                    block.Warnings.Add($"Стр. {rowNumber}: повторная шапка до итогов.");
                block = null; system = null; lastTop = null; captions.Clear();
                continue;
            }
            if (columns is null)
            {
                var line = string.Join(" ", row.Select(Text).Where(t => t.Length > 0));
                if (line.Length > 0) captions.Add(line);
                continue;
            }

            var filled = row.Where(v => !Empty(v)).ToArray();
            if (filled.Length >= 3)
            {
                var seq = filled.Select(Number).ToArray();
                if (seq.All(v => v is not null && v.Value == decimal.Truncate(v.Value)) &&
                    seq[0] == 1 && seq.Select((v, j) => v == j + 1).All(x => x))
                    continue;
            }
            var name = Text(Cell(row, columns.Name));
            if (name.Length == 0)
                foreach (var candidate in columns.Names.AsEnumerable().Reverse().Skip(1))
                    if ((name = Text(Cell(row, candidate))).Length > 0) break;

            if (name.Length == 0 && Empty(Cell(row, columns.Quantity)) && Empty(Cell(row, columns.Unit)) &&
                Number(Cell(row, columns.Total)) is not null)
            {
                var candidate = Text(Cell(row, columns.Price));
                if (TotalKind(candidate) is not null) name = candidate;
            }
            if (name.Length == 0)
            {
                var q = Number(Cell(row, columns.Quantity));
                var fallbackPrice = Number(Cell(row, columns.Price));
                var sum = Number(Cell(row, columns.Total));
                if (q is > 0 && (fallbackPrice is > 0 || sum is > 0) && block is not null)
                {
                    name = $"(без наименования — стр. {rowNumber})";
                    block.Warnings.Add($"Стр. {rowNumber}: есть количество и цена, но нет названия.");
                }
                else
                {
                    if (q is not null || fallbackPrice is not null || sum is not null)
                        (block?.Warnings ?? notes).Add($"Стр. {rowNumber}: числа без названия пропущены.");
                    else if (block is null)
                    {
                        var caption = string.Join(" ", row.Select(Text).Where(t => t.Length > 0));
                        if (caption.Length > 0) captions.Add(caption);
                    }
                    continue;
                }
            }

            var unit = Text(Cell(row, columns.Unit));
            if (unit is "*" or "-" or "—") unit = "";
            var qtyRaw = Cell(row, columns.Quantity);
            var noQty = Empty(qtyRaw);
            var low = name.Trim().TrimEnd(':').ToLowerInvariant();
            var value = Number(Cell(row, columns.Total));
            if (value is null)
                for (var c = row.Count - 1; c >= 0; c--)
                    if (c != columns.Number && Number(row[c]) is decimal v) { value = v; break; }

            if (noQty && unit.Length == 0)
            {
                var kind = TotalKind(low);
                if (kind is not null)
                {
                    if (block is null)
                    {
                        notes.Add($"Стр. {rowNumber}: итог «{name}» без позиций пропущен.");
                        continue;
                    }
                    if (mergeTotals)
                    {
                        if (value is decimal v)
                        {
                            if (!subtotals.TryGetValue(block, out var list))
                                subtotals[block] = list = new();
                            list.Add((rowNumber, kind, v, name, system));
                            if (kind == "smr" && system is not null && !low.Contains("по раздел"))
                                system.Smr = (system.Smr ?? 0m) + v;
                        }
                        if (kind == "total" && low.StartsWith("всего") && low.Contains("ндс"))
                            block = null;
                        continue;
                    }
                    switch (kind)
                    {
                        case "material": block.DeclaredMaterial = value; break;
                        case "transport": block.Transport = value; break;
                        case "smr": block.Smr = value; break;
                        case "pnr": block.Pnr = value; break;
                        case "vat": block.VatDeclared = value; break;
                        case "total":
                            block.DeclaredTotal = value;
                            block.LastRow = rowNumber;
                            ReadVatRate(name, block);
                            block = null; system = null; lastTop = null;
                            break;
                    }
                    continue;
                }

                if (columns.Work is not null && system is not null && !SystemCode.IsMatch(name) &&
                    i + 1 < rows.Count && (name.EndsWith(':') ||
                    Text(Cell(rows[i + 1], columns.Name)).StartsWith("толщ", StringComparison.OrdinalIgnoreCase)))
                {
                    componentCaption = name.TrimEnd(':').Trim();
                    continue;
                }

                if (block is null) { NewBlock(rowNumber); rootSystem = name; }
                else if (name.EndsWith(':') && rootSystem.Length > 0)
                    name = rootSystem + " — " + name.TrimEnd(':').Trim();
                else rootSystem = name;

                system = new ExcelImportSection
                {
                    Name = name,
                    SourceNumber = Text(Cell(row, columns.Number))
                };
                block!.Sections.Add(system);
                lastTop = null;
                componentCaption = "";
                continue;
            }

            if (block is null) NewBlock(rowNumber);
            EnsureSystem();

            var qty = Number(qtyRaw) ?? 0m;
            if (Number(qtyRaw) is null)
                block!.Warnings.Add($"Стр. {rowNumber}: количество не распознано, принято 0 ({name}).");
            var price = Number(Cell(row, columns.Price));
            var rowSum = Number(Cell(row, columns.Total));
            var purchase = Number(Cell(row, columns.PurchasePrice));
            var num = Text(Cell(row, columns.Number));
            var hasNum = !Empty(Cell(row, columns.Number));
            var isChild = (columns.Price is not null || columns.Total is not null || columns.Work is not null) &&
                          !hasNum && price is null && rowSum is null && lastTop is not null;
            var position = new ExcelImportPosition
            {
                Number = hasNum ? num : "", Name = name, Unit = unit, Quantity = qty,
                PurchasePrice = purchase, Note = Text(Cell(row, columns.Note)), SourceRow = rowNumber
            };
            var basisValue = Text(Cell(row, columns.Basis));
            if (basisValue.Length > 0) position.Extra["basis"] = basisValue;
            var used = columns.Used();
            for (var c = 0; c < row.Count; c++)
                if (!used.Contains(c) && !Empty(row[c]))
                    position.Extra[ColumnLetter(c + 1)] = Text(row[c]);
            if (isChild)
            {
                lastTop!.Children.Add(position);
                block!.LastRow = rowNumber;
                continue;
            }
            if (price is null && rowSum is decimal total && qty != 0)
            {
                price = total / qty;
                block!.Warnings.Add($"Стр. {rowNumber}: цена рассчитана из суммы / количества ({name}).");
            }
            position.Price = price ?? 0m;
            if (price is null) missingPrices.Add(rowNumber);
            if (rowSum is decimal declared && Math.Abs(qty * position.Price - declared) > 1m)
                block!.Warnings.Add($"Стр. {rowNumber}: количество × цена не совпадает с суммой ({name}).");
            if (columns.Work is int workIndex)
            {
                position.WorkPrice = Number(Cell(row, workIndex));
                if (position.WorkPrice is null && qty != 0 &&
                    Number(Cell(row, columns.WorkTotal)) is decimal workTotal)
                    position.WorkPrice = ExcelImportSection.DecimalRound(workTotal / qty);
            }
            if (componentCaption.Length > 0 &&
                (name.StartsWith("толщ", StringComparison.OrdinalIgnoreCase) || char.IsLower(name[0])))
                position.Name = componentCaption + ": " + name;
            system!.Positions.Add(position);
            lastTop = position;
            block!.LastRow = rowNumber;
        }

        if (mergeTotals)
            foreach (var b in blocks)
            {
                if (!subtotals.TryGetValue(b, out var totals) || totals.Count == 0) continue;
                var lastPosition = b.Sections.SelectMany(s => s.Positions)
                    .SelectMany(p => new[] { p.SourceRow }.Concat(p.Children.Select(c => c.SourceRow))).DefaultIfEmpty(0).Max();
                decimal? Choose(string kind)
                {
                    var trailing = totals.Where(t => t.Kind == kind && t.Row > lastPosition &&
                        !t.Label.Contains("по раздел", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (trailing.Count > 0) return trailing[^1].Value;
                    var sections = totals.Where(t => t.Kind == kind && t.Label.Contains("по раздел", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (sections.Count > 0) return sections.Sum(t => t.Value);
                    return totals.Where(t => t.Kind == kind && t.Row <= lastPosition &&
                        !t.Label.Contains("по раздел", StringComparison.OrdinalIgnoreCase)).Sum(t => t.Value);
                }
                b.DeclaredMaterial = Choose("material");
                b.Transport = Choose("transport");
                b.Smr = Choose("smr");
                b.VatDeclared = Choose("vat");
                b.DeclaredTotal = Choose("total");
                b.LastRow = totals.Max(t => t.Row);
                var grand = totals.LastOrDefault(t => t.Kind == "total").Label;
                if (grand is not null) ReadVatRate(grand, b);
                string root = "";
                for (var i = 0; i < b.Sections.Count; i++)
                {
                    var s = b.Sections[i];
                    if (s.Positions.Count == 0 && i + 1 < b.Sections.Count) root = s.Name;
                    else if (root.Length > 0) s.Parent = root;
                }
            }
        if (columns is null) notes.Add("Не найдена шапка таблицы с наименованием и количеством.");
        if (missingPrices.Count > 0)
            (blocks.LastOrDefault()?.Warnings ?? notes).Add($"Нет цен у {missingPrices.Count} строк: {string.Join(", ", missingPrices.Take(10))}.");
        blocks.RemoveAll(b => b.PositionCount == 0);
        foreach (var b in blocks) ValidateTotals(b);
        return (blocks, notes);
    }

    private static Header? DetectHeader(IReadOnlyList<object?> row)
    {
        var h = new Header();
        for (var i = 0; i < row.Count; i++)
        {
            var t = Text(row[i]).ToLowerInvariant();
            if (t.Contains("наименование")) h.Names.Add(i);
            else if (t.StartsWith("поз") || t is "№" or "№ п/п" or "n") h.Number ??= i;
            else if (t.StartsWith("ед")) h.Unit = i;
            else if (t.StartsWith("кол") || t.StartsWith("объем") || t.StartsWith("объём")) h.Quantity = i;
            else if (t.StartsWith("примеч")) h.Note = i;
            else if (t.StartsWith("обоснование")) h.Basis = i;
            else if (t.StartsWith("цена")) h.Prices.Add(i);
            else if (t.StartsWith("всего") || t.StartsWith("сумма") || t.StartsWith("стоимость")) h.Totals.Add(i);
        }
        return h.Names.Count > 0 && h.Quantity is not null &&
            (h.Prices.Count > 0 || h.Totals.Count > 0 || h.Unit is not null) ? h : null;
    }

    private static bool RefineHeader(Header h, IReadOnlyList<object?> row, IReadOnlyList<object?> next)
    {
        var groups = new List<(int Index, string Kind)>();
        for (var i = 0; i < row.Count; i++)
        {
            var t = Text(row[i]).ToLowerInvariant();
            if (t.StartsWith("стоимость") || t.StartsWith("цена") || t is "материалы" or "работы")
            {
                if (t.Contains("работ") || t.Contains("смр") || t.Contains("монтаж")) groups.Add((i, "work"));
                else if (t.Contains("материал") || t.Contains("оборудован")) groups.Add((i, "mat"));
            }
        }
        if (!groups.Any(g => g.Kind == "work") ||
            next.Any(v => Text(v).Contains("наименование", StringComparison.OrdinalIgnoreCase))) return false;
        groups.Sort((a, b) => a.Index.CompareTo(b.Index));
        (int? Unit, int? Total) Search(int start, int end)
        {
            int? unit = null, total = null;
            for (var i = start; i < Math.Min(end, next.Count); i++)
            {
                var t = Text(next[i]).ToLowerInvariant();
                if (t.Contains("ед") || t.StartsWith("цена")) unit ??= i;
                else if (t.StartsWith("всего") || t.StartsWith("сумма") || t.StartsWith("стоимость")) total ??= i;
            }
            return (unit, total);
        }
        int? mat = null, matTotal = null, work = null, workTotal = null;
        for (var j = 0; j < groups.Count; j++)
        {
            var g = groups[j];
            var values = Search(g.Index, j + 1 < groups.Count ? groups[j + 1].Index : next.Count);
            if (g.Kind == "mat") (mat, matTotal) = values;
            else (work, workTotal) = values;
        }
        if (work is null) return false;
        h.Prices.Clear(); h.Totals.Clear();
        if (mat is int mp) h.Prices.Add(mp);
        if (matTotal is int mt) h.Totals.Add(mt);
        h.Work = work; h.WorkTotal = workTotal;
        return true;
    }

    private static void DetectMetadata(IEnumerable<string> meta, ExcelImportBlock block)
    {
        var text = string.Join(" ", meta);
        if (text.Contains("дополнительн", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("соглашени", StringComparison.OrdinalIgnoreCase))
        {
            block.Kind = "dop";
            block.Number = Regex.Match(text, @"соглашени\w*\s*№\s*([^\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        }
        else if (text.Contains("договор", StringComparison.OrdinalIgnoreCase) &&
                 text.Contains("приложени", StringComparison.OrdinalIgnoreCase))
        {
            block.Kind = "main";
            block.Number = Regex.Match(text, @"договор\w*\s*(?:подряда\s*)?№\s*([^\s,]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        }
        if (block.Kind is "main" or "dop")
            block.Date = DateRegex.Match(text).Value;
        if (Regex.IsMatch(text, @"подряд\w*\s*№\s*[рr]4/", RegexOptions.IgnoreCase))
            block.Form = "r4";
    }

    private static string? TotalKind(string text)
    {
        var t = text.Trim().TrimEnd(':').ToLowerInvariant();
        if (t.StartsWith("итого за материал") || t.StartsWith("итого материал") ||
            t.StartsWith("итого по материал") || t is "материал" or "материалы") return "material";
        if (t.StartsWith("транспорт")) return "transport";
        if (t.StartsWith("пуско-налад") || t.StartsWith("пусконалад") ||
            t.StartsWith("пуско налад") || t.StartsWith("пнр")) return "pnr";
        if (t.StartsWith("смр") || t.StartsWith("монтажные работы") ||
            t.StartsWith("стоимость смр") || t.StartsWith("итого смр")) return "smr";
        if (t.Contains("ндс") && (t.StartsWith("в т") || t.StartsWith("ндс"))) return "vat";
        if (t.StartsWith("итого") || t.StartsWith("всего")) return "total";
        return null;
    }

    private static void ReadVatRate(string text, ExcelImportBlock block)
    {
        var match = VatRegex.Match(text);
        if (match.Success && decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var value)) block.VatRate = value;
    }

    private static void ValidateTotals(ExcelImportBlock block)
    {
        var mat = block.MaterialSum;
        if (block.DeclaredMaterial is decimal dm)
            block.Checks.Add(new ExcelImportCheck(Math.Abs(mat - dm) <= 1m,
                $"Материалы {mat:N2} / заявлено {dm:N2}"));
        if (block.DeclaredTotal is decimal dt)
            block.Checks.Add(new ExcelImportCheck(Math.Abs(block.CalculatedTotal - dt) <= 1m,
                $"Итого {block.CalculatedTotal:N2} / заявлено {dt:N2}"));
        if (block.DeclaredMaterial is null && block.DeclaredTotal is null)
            block.Checks.Add(new ExcelImportCheck(block.VolumesOnly, "Нет итоговых строк для сверки."));
        if (block.Form == "dns" && block.VatDeclared is decimal vat)
        {
            var expectedVat = ExcelImportSection.DecimalRound(block.DnsRowsSum * block.VatRate / 100m);
            block.Checks.Add(new ExcelImportCheck(Math.Abs(expectedVat - vat) <= 1m,
                $"НДС сверху {expectedVat:N2} / заявлено {vat:N2}"));
        }
        var invalid = block.Sections.SelectMany(s => s.Positions).Count(p =>
            p.Quantity < 0 || p.Price < 0 || p.PurchasePrice < 0 || p.WorkPrice < 0);
        if (invalid > 0) block.Checks.Add(new ExcelImportCheck(false, $"Отрицательные количество/цены: {invalid}."));
    }

    private static string Text(object? value)
    {
        if (value is null || value is DBNull) return "";
        if (value is DateTime dt) return dt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (value is double d && d == Math.Truncate(d) && Math.Abs(d) < 1e12)
            return d.ToString("0", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture)?.Replace('\u00a0', ' ').Trim() ?? "";
    }

    private static object? Cell(IReadOnlyList<object?> row, int? index) =>
        index is >= 0 && index.Value < row.Count ? row[index.Value] : null;
    private static bool Empty(object? value) => Text(value) is "" or "*" or "-" or "—" or "–";
    private static decimal? Number(object? value)
    {
        if (value is null or bool) return null;
        var s = Text(value).Replace(" ", "").Replace(",", ".").Replace("₽", "");
        s = Regex.Replace(s, @"(?:руб\.?|р\.?)$", "", RegexOptions.IgnoreCase);
        if (!NumberRegex.IsMatch(s)) return null;
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var n) || Math.Abs(n) > 1_000_000_000_000m) return null;
        return n;
    }

    private static string ColumnLetter(int column)
    {
        var text = "";
        while (column > 0) { column--; text = (char)('A' + column % 26) + text; column /= 26; }
        return text;
    }
}
