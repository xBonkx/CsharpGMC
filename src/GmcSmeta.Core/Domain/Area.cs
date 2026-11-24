using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Площадь (м²) строк сметы — перенос python/smeta/area.py.
/// Площадь выводится из названия (размеры) и единицы:
///   воздуховод, м   — периметр × длину (прямоугольник 2·(A+B)/1000, круг π·D/1000);
///   фасонка, шт     — типовые формулы (отвод, переход, врезка, тройник, заглушка…);
///   что-то в м²     — само количество.
/// Вписанная вручную площадь (area_manual) всегда важнее.
/// Расчёты здесь на double, как в Python: это геометрия, а не деньги; сравнение
/// с Python-версией — до третьего знака (столько хранит количество сводной строки).
/// </summary>
public static class Area
{
    private const double PEnd = 0.05;

    private static readonly Regex Rect = new(@"(\d{2,4})\s*[xх×*]\s*(\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex RoundSec = new(@"(?:⌀|Ø|ø|ф|диам\.?|d\s*=?)\s*(\d{2,4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Thick = new(@"(?:\bb|δ|s|толщ(?:ина|\.)?)\s*=?\s*(\d+(?:[.,]\d+)?)\s*(?:мм)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Angle = new(@"(\d{2,3})\s*°",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Ei = new(@"\bE[IlI]\s*(\d{2,3})\b|\bEI(\d{2,3})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] DuctWords = { "воздуховод" };

    private static readonly string[] FittingWords =
    {
        "отвод", "переход", "врезка", "тройник", "заглушка", "утка", "крестовина", "ниппель",
        "муфта", "колено", "поворот",
    };

    private static readonly string[] FireWords = { "огнезащ", "огнестойк", "огнезащит" };

    private static readonly string[] InsulWords =
        { "теплоизол", "изоляц", "k-flex", "кфлекс", "энергофлекс", "армафлекс" };

    public const string AllSystems = "Воздуховоды, фасонные изделия и изоляция (м²)";

    private static string Low(string? text) => (text ?? "").ToLowerInvariant().Replace('ё', 'е');

    // ---------------------------------------------------------------- сечения

    /// <summary>("rect", A, B) или ("round", D, D) в миллиметрах, в порядке появления в названии.</summary>
    public static IReadOnlyList<(string Kind, double A, double B)> Sections(string? name)
    {
        var found = new List<(int At, (string, double, double) Section)>();
        var spans = new List<(int Start, int End)>();
        var text = name ?? "";

        foreach (Match m in Rect.Matches(text))
        {
            found.Add((m.Index, ("rect", Num(m.Groups[1].Value), Num(m.Groups[2].Value))));
            spans.Add((m.Index, m.Index + m.Length));
        }
        foreach (Match m in RoundSec.Matches(text))
        {
            if (spans.Any(s => s.Start <= m.Index && m.Index < s.End)) continue;
            found.Add((m.Index, ("round", Num(m.Groups[1].Value), Num(m.Groups[1].Value))));
        }
        return found.OrderBy(x => x.At).Select(x => x.Section).ToList();
    }

    private static double Num(string text) =>
        double.Parse(text.Replace(',', '.'), CultureInfo.InvariantCulture);

    public static double Perimeter((string Kind, double A, double B) section) =>
        (section.Kind == "rect" ? 2 * (section.A + section.B) : Math.PI * section.A) / 1000.0;

    private static double Width((string Kind, double A, double B) section) =>
        Math.Max(section.A, section.B) / 1000.0;

    private static double Cross((string Kind, double A, double B) section) =>
        (section.Kind == "rect" ? section.A * section.B : Math.PI * section.A * section.A / 4) / 1e6;

    public static bool IsDuctFamily(string? name)
    {
        var low = Low(name);
        if (FireWords.Concat(InsulWords).Any(w => low.Contains(w, StringComparison.Ordinal))) return false;
        // Важно НАЧАЛО названия: «Сетка … на воздуховод» — не воздуховод.
        var first = low.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return DuctWords.Concat(FittingWords).Any(w => first.StartsWith(w, StringComparison.Ordinal));
    }

    /// <summary>(м² на 1 единицу, как посчитано) или (null, почему не считается).</summary>
    public static (double? Value, string How) AutoArea(string? name, string? unit)
    {
        var u = Low(unit).Replace(" ", "", StringComparison.Ordinal).TrimEnd('.')
            .Replace("²", "2", StringComparison.Ordinal);
        if (u is "м2" or "кв.м" or "квм") return (1.0, "уже в м²");

        var low = Low(name);
        var sections = Sections(name);
        if (u is "м" or "пм" or "п.м" or "мп")
        {
            if (!IsDuctFamily(name)) return (null, "в метрах, но это не воздуховод");
            if (sections.Count == 0) return (null, "в названии нет сечения (200x100 или ⌀160)");
            var first = sections[0];
            return (Perimeter(first), $"периметр сечения {SectionText(first)}");
        }
        if (u is not ("шт" or "компл" or "к-т"))
            return (null, $"единица «{unit}» — площадь не считается");
        if (sections.Count == 0) return (null, "в названии нет сечения");
        return FittingArea(low, name, sections);
    }

    // ---------------------------------------------------------------- фасонные изделия

    private static double Bend((string Kind, double A, double B) section, double angleDeg)
    {
        var th = angleDeg * Math.PI / 180.0;
        var (kind, a0, b0) = section;
        if (kind == "round")
        {
            var d = a0 / 1000;
            return th * d * Math.PI * d + 2 * PEnd * Math.PI * d;   // тор с радиусом оси r = D
        }
        var a = a0 / 1000;
        var b = b0 / 1000;
        const double rIn = 0.05;
        var rOut = 0.05 + a;
        return th * ((rOut * rOut - rIn * rIn) + (rOut + rIn) * b) + 2 * PEnd * Perimeter(section);
    }

    private static double Transition((string Kind, double A, double B) s1,
                                     (string Kind, double A, double B) s2, double length = 0.3)
    {
        if (s1.Kind == "rect" && s2.Kind == "rect")
        {
            var a = s1.A / 1000; var b = s1.B / 1000;
            var a1 = s2.A / 1000; var b1 = s2.B / 1000;
            var fa = (a + a1) / 2 * Hypot(length, (b - b1) / 2) * 2;   // четыре трапеции с наклоном
            var fb = (b + b1) / 2 * Hypot(length, (a - a1) / 2) * 2;
            return fa + fb + PEnd * (Perimeter(s1) + Perimeter(s2));
        }
        if (s1.Kind == "round" && s2.Kind == "round")
        {
            var r1 = s1.A / 2000; var r2 = s2.A / 2000;
            return Math.PI * (r1 + r2) * Hypot(length, r1 - r2) + PEnd * (Perimeter(s1) + Perimeter(s2));
        }
        var rect = s1.Kind == "rect" ? s1 : s2;      // прямоугольник ↔ круг
        var rnd = s1.Kind == "rect" ? s2 : s1;
        var pm = (Perimeter(rect) + Perimeter(rnd)) / 2;
        return pm * Hypot(length, (Math.Max(rect.A, rect.B) - rnd.A) / 2000) + PEnd * pm;
    }

    private static double Hole((string Kind, double A, double B) section) =>
        (section.Kind == "rect" ? section.A * section.B : section.A * section.A / 2) / 1e6;

    private static (double? Value, string How) FittingArea(string low, string? name,
        IReadOnlyList<(string Kind, double A, double B)> sections)
    {
        var s0 = sections[0];
        if (low.Contains("отвод", StringComparison.Ordinal) ||
            low.Contains("колено", StringComparison.Ordinal) ||
            low.Contains("поворот", StringComparison.Ordinal))
        {
            var m = Angle.Match(name ?? "");
            var angle = m.Success ? Num(m.Groups[1].Value) : 90.0;
            return (Bend(s0, angle), $"отвод {G(angle)}° {SectionText(s0)} (как на helpeng.ru)");
        }
        if (low.Contains("переход", StringComparison.Ordinal))
        {
            var s1 = sections.Count > 1 ? sections[1] : s0;
            return (Transition(s0, s1), $"переход {SectionText(s0)}–{SectionText(s1)}, длина 300 мм");
        }
        if (low.Contains("врезка", StringComparison.Ordinal))   // ответвление тройника, 100 мм
            return (Perimeter(s0) * 0.1, $"врезка {SectionText(s0)}: периметр × 0,1 м");
        if (low.Contains("тройник", StringComparison.Ordinal) ||
            low.Contains("крестовина", StringComparison.Ordinal))
        {
            var main = sections[0];
            var br = sections.Count > 1 ? sections[1] : sections[0];
            var nBr = low.Contains("крестовина", StringComparison.Ordinal) ? 2 : 1;
            var length = Math.Max(0.3, Width(br) + 0.1);
            return (Perimeter(main) * length - nBr * Hole(br) + nBr * Perimeter(br) * 0.1,
                $"{(nBr == 2 ? "крестовина" : "тройник")}: корпус {length.ToString("0.00", CultureInfo.InvariantCulture)} м + ответвление 0,1 м");
        }
        if (low.Contains("заглушка", StringComparison.Ordinal))
            return (Cross(s0) + 0.02 * Perimeter(s0), $"заглушка {SectionText(s0)}: сечение + отбортовка 20 мм");
        if (low.Contains("утка", StringComparison.Ordinal))
        {
            const double l = 0.3, l1 = 0.1;
            if (s0.Kind == "round")
                return (Perimeter(s0) * Hypot(l, l1), "утка: длина 300, смещение 100 мм");
            var a = s0.A / 1000;
            var b = s0.B / 1000;
            return (2 * a * Hypot(l, l1) + 2 * b * l, "утка: длина 300, смещение 100 мм");
        }
        if (low.Contains("ниппель", StringComparison.Ordinal) ||
            low.Contains("муфта", StringComparison.Ordinal))
            return (Perimeter(s0) * 0.1, "ниппель/муфта: периметр × 0,1 м");
        return (null, "непонятный тип изделия");
    }

    private static string SectionText((string Kind, double A, double B) section) =>
        section.Kind == "rect" ? $"{G(section.A)}x{G(section.B)}" : $"⌀{G(section.A)}";

    /// <summary>Как «:g» в Python: целое без «,0», дробное — без лишних нулей.</summary>
    private static string G(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- материал и сводные строки

    /// <summary>Толщина стенки, мм: из названия (b=0.8, δ=0,7), иначе по размеру (СП 60.13330).</summary>
    public static double SteelThickness(string? name)
    {
        var m = Thick.Match(name ?? "");
        if (m.Success && double.TryParse(m.Groups[1].Value.Replace(',', '.'),
                NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            if (v >= 0.3 && v <= 3) return v;
        var sections = Sections(name);
        if (sections.Count == 0) return 0.5;
        var (kind, a, b) = sections[0];
        var big = Math.Max(a, b);
        if (kind == "round")
            return big <= 200 ? 0.5 : big <= 450 ? 0.6 : big <= 800 ? 0.7 : 1.0;
        return big <= 250 ? 0.5 : big <= 1000 ? 0.7 : 0.9;
    }

    /// <summary>(ключ группы, название сводной строки) или null, если строка не сворачивается в м².</summary>
    public static (string Key, string Title)? Material(string? name, string? unit)
    {
        var low = Low(name);
        if (FireWords.Any(w => low.Contains(w, StringComparison.Ordinal)) ||
            (Ei.IsMatch(name ?? "") && low.Contains("покрыт", StringComparison.Ordinal)))
        {
            var m = Ei.Match(name ?? "");
            var ei = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : "";
            return ($"fire:{ei}", ei.Length > 0 ? $"Огнезащита воздуховодов EI{ei}" : "Огнезащита воздуховодов");
        }
        if (InsulWords.Any(w => low.Contains(w, StringComparison.Ordinal)))
        {
            var m = Regex.Match(name ?? "", @"(\d{1,3})\s*мм", RegexOptions.CultureInvariant);
            if (!m.Success) m = Thick.Match(name ?? "");
            var th = m.Success ? m.Groups[1].Value.Replace(',', '.') : "";
            return ($"insul:{th}", th.Length > 0 ? $"Теплоизоляция воздуховодов δ={th} мм" : "Теплоизоляция воздуховодов");
        }
        if (IsDuctFamily(name))
        {
            var (area, _) = AutoArea(name, unit);
            if (area is null) return null;
            var metal = low.Contains("нерж", StringComparison.Ordinal) ? "нержавеющей стали"
                : low.Contains("черн", StringComparison.Ordinal) ? "черной стали"
                : "оцинкованной стали";
            var th = SteelThickness(name);
            var thText = G(th).Replace(".", ",");
            return ($"steel:{metal}:{G(th)}", $"Воздуховоды и фасонные изделия из {metal} δ={thText} мм");
        }
        return null;
    }

    /// <summary>Площадь на единицу: вписанная вручную важнее расчётной.</summary>
    public static (double? Value, string How) AreaPerUnit(string? name, string? unit, double? areaManual)
    {
        if (areaManual is not null) return (areaManual, "вписано вручную");
        return AutoArea(name, unit);
    }

    /// <summary>Площадь строки (м²) или null, если её не посчитать.</summary>
    public static double? RowArea(string? name, string? unit, double qty, double? areaManual)
    {
        var (area, _) = AreaPerUnit(name, unit, areaManual);
        return area is null ? null : area * qty;
    }

    /// <summary>Сводная строка «м²»: в extra_json есть ключ m2.</summary>
    public static bool IsSummary(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return false;
        try
        {
            return JsonNode.Parse(extraJson) is JsonObject node && node["m2"] is not null;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Лежит ли в базе сводная строка «м²» с этим id.</summary>
    public static bool IsSummary(SqliteConnection conn, long positionId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT extra_json FROM positions WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", positionId);
        var found = cmd.ExecuteScalar();
        return found is string json && IsSummary(json);
    }

    /// <summary>
    /// Строка внутри сводной «м²» изменилась — количество сводной строки следует за ней.
    /// Как area.refresh_summary.
    /// </summary>
    public static void RefreshSummary(SqliteConnection conn, long summaryId)
    {
        if (!IsSummary(conn, summaryId)) return;

        var rows = new List<(string Name, string Unit, double Qty, double? Manual)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name, unit, qty, area_manual FROM positions WHERE parent_id = $id";
            cmd.Parameters.AddWithValue("$id", summaryId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((
                    r.IsDBNull(0) ? "" : r.GetString(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetDouble(2),
                    r.IsDBNull(3) ? null : r.GetDouble(3)));
        }
        var total = rows.Sum(x => RowArea(x.Name, x.Unit, x.Qty, x.Manual) ?? 0);
        RowWriter.Update(conn, "positions", summaryId,
            new Dictionary<string, object?> { ["qty"] = PythonRound(total, 3) });
    }

    /// <summary>SMR «по позициям»: сумма работ строк акта, как acts.sync_work.</summary>
    public static void SyncActSmr(SqliteConnection conn, long actId)
    {
        var total = 0m;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT qty, work_price FROM act_lines WHERE act_id = $id";
            cmd.Parameters.AddWithValue("$id", actId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var qty = r.IsDBNull(0) ? 0m : Money.FromDouble(r.GetDouble(0));
                var work = r.IsDBNull(1) ? 0m : Money.FromDouble(r.GetDouble(1));
                total += Money.Round2(qty * work);
            }
        }
        RowWriter.Update(conn, "acts", actId,
            new Dictionary<string, object?> { ["smr_amount"] = Money.Round2(total) });
    }

    /// <summary>round(x, n) в Python: половина — к чётному. Для площадей расхождение невозможно проверить глазом.</summary>
    private static double PythonRound(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.ToEven);

    /// <summary>math.hypot в Python — тот же корень из суммы квадратов.</summary>
    private static double Hypot(double a, double b) =>
        Math.Sqrt(a * a + b * b);
}
