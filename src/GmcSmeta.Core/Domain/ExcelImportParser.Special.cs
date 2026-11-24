using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Specialized Python importer forms: DNS and quantities-only Revit schedules.</summary>
public static partial class ExcelImportParser
{
    private static readonly HashSet<string> RevitUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "шт", "шт.", "м", "м.", "п.м", "п.м.", "пм", "мп", "м.п", "м.п.",
        "м²", "м2", "кв.м", "кв.м.", "м.кв", "м.кв.", "м³", "м3", "кг", "кг.",
        "т", "компл", "компл.", "к-т", "к-кт", "л", "уп", "уп."
    };

    private static string? RevitUnit(object? value)
    {
        var t = Text(value).Replace(" ", "").ToLowerInvariant();
        if (!RevitUnits.Contains(t)) return null;
        return t switch
        {
            "м²" or "кв.м" or "кв.м." or "м.кв" or "м.кв." => "м2",
            "м³" => "м3",
            "п.м" or "п.м." or "м." or "пм" or "мп" or "м.п" or "м.п." => "м",
            "шт." => "шт", "кг." => "кг", "компл." or "к-т" or "к-кт" => "компл",
            "уп." => "уп", _ => t
        };
    }

    private static ExcelImportBlock? TryParseRevit(string sheet, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var width = rows.Select(r => r.Count).DefaultIfEmpty(0).Max();
        if (width < 4) return null;
        var moneyWords = new[] { "цена", "стоимост", "сумма", "итого", "всего", "руб" };
        if (rows.Take(400).Any(r => r.Any(v =>
        {
            var t = Text(v).ToLowerInvariant();
            return t.Length is > 0 and < 60 && moneyWords.Any(w => t.Contains(w));
        }))) return null;
        var hits = new int[width];
        foreach (var row in rows)
            for (var c = 0; c + 1 < row.Count; c++)
                if (RevitUnit(row[c]) is not null && Number(row[c + 1]) is not null) hits[c]++;
        var u = Array.IndexOf(hits, hits.Max());
        if (u < 2 || hits[u] < 3) return null;
        var block = new ExcelImportBlock { Sheet = sheet, Title = "Объёмы из Ревита",
            VolumesOnly = true, FirstRow = 0 };
        ExcelImportSection? system = null;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var name = Text(Cell(row, 1));
            var unit = RevitUnit(Cell(row, u));
            var qty = Number(Cell(row, u + 1));
            if (name.Length > 0 && unit is not null && qty is decimal quantity)
            {
                if (system is null)
                {
                    system = new ExcelImportSection { Name = "Без названия" };
                    block.Sections.Add(system);
                    block.Warnings.Add($"Стр. {i + 1}: позиции без названия системы.");
                }
                var mark = Text(Cell(row, u > 2 ? 2 : null));
                var full = mark.Length > 0 && !name.Contains(mark, StringComparison.OrdinalIgnoreCase)
                    ? name + " " + mark : name;
                var maker = u == 5 ? Text(Cell(row, 4)).Trim('«', '»', '"', '\'') : "";
                var note = Text(Cell(row, u + 3));
                var p = new ExcelImportPosition
                {
                    Name = Regex.Replace(full, @"\s+", " ").Trim(), Unit = unit, Quantity = quantity,
                    SourceRow = i + 1, Note = string.Join("; ", new[] { maker, note }.Where(s => s.Length > 0))
                };
                p.Extra["src"] = p.Name;
                system.Positions.Add(p);
                block.FirstRow = block.FirstRow == 0 ? i + 1 : block.FirstRow;
                block.LastRow = i + 1;
                continue;
            }
            var single = row.Select((v, col) => (Value: Text(v), Col: col))
                .Where(x => x.Value.Length > 0).ToArray();
            if (single.Length == 1 && single[0].Col == 0)
            {
                var heading = single[0].Value;
                if (i == 0 || heading.StartsWith("спецификация", StringComparison.OrdinalIgnoreCase))
                    continue;
                system = new ExcelImportSection { Name = heading };
                block.Sections.Add(system);
            }
        }
        block.Sections.RemoveAll(s => s.Positions.Count == 0);
        if (block.PositionCount < 3 || block.Sections.Count < 2 &&
            !rows.Take(10).Any(r => r.Any(v => Text(v).StartsWith("спецификация", StringComparison.OrdinalIgnoreCase))))
            return null;
        block.Warnings.Insert(0, $"Спецификация Revit: {block.PositionCount} строк в {block.Sections.Count} системах. " +
            "Загружаются только количества; цены остаются нулевыми.");
        block.Checks.Add(new ExcelImportCheck(true,
            $"Количество строк: {block.PositionCount}; систем: {block.Sections.Count}."));
        return block;
    }

    private static ExcelImportBlock? TryParseDns(string sheet, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var headerRow = -1;
        var subRow = -1;
        for (var i = 0; i < Math.Min(rows.Count, 60); i++)
        {
            var head = rows[i].Select(Text).Select(s => s.ToLowerInvariant()).ToArray();
            if (!head.Any(s => s.StartsWith("обоснование")) ||
                !head.Any(s => s.Contains("наименование"))) continue;
            for (var j = i; j < Math.Min(rows.Count, i + 4); j++)
            {
                var sub = string.Join(" | ", rows[j].Select(Text)).ToLowerInvariant();
                if (sub.Contains("работы за ед") && sub.Contains("материал за ед"))
                { headerRow = i; subRow = j; break; }
            }
            if (headerRow >= 0) break;
        }
        if (headerRow < 0) return null;
        var headCells = rows[headerRow].Select(Text).Select(s => s.ToLowerInvariant()).ToArray();
        var subCells = rows[subRow].Select(Text).Select(s => s.ToLowerInvariant()).ToArray();
        int? Find(string[] cells, params string[] values)
        {
            for (var c = 0; c < cells.Length; c++)
                if (values.Any(word => cells[c].StartsWith(word))) return c;
            return null;
        }
        var ncol = Find(headCells, "№");
        var basisCol = Find(headCells, "обоснование");
        var nameCol = Find(headCells, "наименование");
        var unitCol = Find(headCells, "единица", "ед");
        var qtyCol = Find(headCells, "количество", "кол");
        var workCol = Find(subCells, "работы за ед");
        var materialCol = Find(subCells, "материал за ед");
        if (nameCol is null || qtyCol is null || workCol is null || materialCol is null) return null;
        var title = rows.Take(headerRow).Select(r => string.Join(" ", r.Select(Text).Where(t => t.Length > 0)))
            .FirstOrDefault(s => s.StartsWith("на ", StringComparison.OrdinalIgnoreCase));
        var block = new ExcelImportBlock { Sheet = sheet, Form = "dns", Title = title?.Length > 3 ? title[3..].Trim() : sheet,
            FirstRow = headerRow + 1 };
        ExcelImportSection? section = null, current = null;
        for (var i = subRow + 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var filled = row.Select(Text).Where(s => s.Length > 0).ToArray();
            if (filled.Length == 0) continue;
            var first = filled[0];
            var low = first.ToLowerInvariant();
            var num = Number(Cell(row, ncol));
            var name = Text(Cell(row, nameCol));
            if (num is null || name.Length == 0)
            {
                var last = row.Select(Number).Where(n => n is not null).LastOrDefault();
                if (low.StartsWith("итого") && last is decimal total)
                {
                    block.DeclaredTotal = total;
                    block.LastRow = i + 1;
                    continue;
                }
                var vatMatch = VatRegex.Match(first);
                if (vatMatch.Success && last is decimal vat)
                {
                    ReadVatRate(first, block);
                    block.VatDeclared = vat;
                    continue;
                }
                if ((low.Contains("стоимость материал") || low.Contains("стоимость работ")) && last is not null)
                    continue;
                var group = Regex.Match(first, @"^раздел\s*\d+[.)]?\s*(?:система\s+)?(.+)", RegexOptions.IgnoreCase);
                if (group.Success)
                {
                    section = new ExcelImportSection { Name = group.Groups[1].Value.Trim(),
                        Heading = Regex.Replace(first, @"\s+", " ").Trim() };
                    block.Sections.Add(section);
                    current = section;
                    continue;
                }
                current = new ExcelImportSection { Name = first,
                    Parent = section is not null && section.Positions.Count == 0 ? section.Name : "" };
                block.Sections.Add(current);
                continue;
            }
            if (current is null)
            {
                current = new ExcelImportSection { Name = "Без системы" };
                block.Sections.Add(current);
            }
            var work = Number(Cell(row, workCol));
            var material = Number(Cell(row, materialCol));
            var pos = new ExcelImportPosition
            {
                Number = Text(Cell(row, ncol)), Name = Regex.Replace(name, @"\s+", " ").Trim(),
                Unit = Text(Cell(row, unitCol)), Quantity = Number(Cell(row, qtyCol)) ?? 0m,
                Price = material is > 0 ? material.Value : work ?? 0m, SourceRow = i + 1
            };
            var basis = Text(Cell(row, basisCol));
            if (basis.Length > 0) pos.Extra["basis"] = basis;
            pos.Extra["kind"] = work is > 0 && material is not > 0 ? "work" : "material";
            current.Positions.Add(pos);
            block.LastRow = i + 1;
        }
        block.Sections.RemoveAll(s => s.Positions.Count == 0 && !block.Sections.Any(x => x.Parent == s.Name));
        if (block.PositionCount == 0) return null;
        ValidateTotals(block);
        return block;
    }

    private static void ApplyVatOnTop(ExcelImportBlock block, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var lines = rows.Select(r => string.Join(" ", r.Select(Text).Where(s => s.Length > 0)).ToLowerInvariant()).ToArray();
        if (!lines.Any(s => s.Contains("итого") && s.Contains("без ндс")) ||
            !lines.Any(s => VatRegex.IsMatch(s) && !s.Contains("без ндс"))) return;
        block.Form = "dns";
        for (var i = 0; i < rows.Count; i++)
        {
            var line = lines[i];
            var number = rows[i].Select(Number).Where(x => x is not null).LastOrDefault();
            if (number is null) continue;
            if (line.StartsWith("итого") && !line.Contains("без ндс"))
                block.DeclaredTotal = number;
            else if (VatRegex.IsMatch(line) && !line.Contains("без ндс"))
            {
                ReadVatRate(line, block);
                block.VatDeclared = number;
            }
        }
        block.Checks.Clear();
        ValidateTotals(block);
    }
}
