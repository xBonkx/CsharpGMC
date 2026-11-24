using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmcSmeta.Core.Domain;

/// <summary>Python passports/model.py schema. Values are strings to preserve entered punctuation.</summary>
public sealed class VentPassport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long ObjectId { get; set; }
    public string System { get; set; } = "";
    public string Object { get; set; } = "";
    public string Address { get; set; } = "";
    public string Organization { get; set; } = "";
    public string Zone { get; set; } = "";
    public string Location { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Date { get; set; } = "";
    public bool Filter { get; set; }
    public List<VentEquipment> Equipment { get; set; } = VentPassports.NewEquipment();
    public List<VentFlowPoint> Points { get; set; } = new();
    [JsonIgnore] public long Revision { get; set; }
}

public sealed class VentEquipment
{
    public List<string> Project { get; set; } = new();
    public List<string> Actual { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class VentFlowPoint
{
    public string Section { get; set; } = "";
    public string Room { get; set; } = "";
    public string Project { get; set; } = "";
    public string Actual { get; set; } = "";
}

public sealed class VentEquipmentTemplate
{
    public string Key { get; set; } = "";
    public string ProjectModel { get; set; } = "";
    public string ActualModel { get; set; } = "";
    public List<VentEquipment> Equipment { get; set; } = VentPassports.NewEquipment();
}

public static class VentPassports
{
    public sealed record SectionSpec(string Title, string[] Parameters);

    public static readonly SectionSpec[] Sections =
    [
        new("Вентилятор",
        ["Тип","№","Диаметр колеса, мм","Подача, м³/ч","Полное давление, Па","Диаметр шкива, мм","Частота вращения, мин⁻¹"]),
        new("Электродвигатель",
        ["Тип","Мощность, кВт","Фазность","Ток, А","Частота вращения, мин⁻¹","Диаметр шкива, мм","Частотный регулятор","Вид передачи"]),
        new("Воздухонагреватели",
        ["Тип / модель","Количество","Мощность, кВт","Воздух до, °C","Воздух после, °C","Теплоноситель до, °C","Теплоноситель после, °C"]),
        new("Воздухоохладители",
        ["Тип / модель","Количество","Мощность, кВт","Воздух до, °C","Воздух после, °C","Энтальпия до, кДж/кг","Энтальпия после, кДж/кг","Холодоноситель до, °C","Холодоноситель после, °C"]),
        new("Увлажнитель",
        ["Тип / марка","Мощность нагревателя, кВт","Пар, кг/ч","Насос: тип, мощность, кВт","Расход воды, м³/ч","Давление, кПа"]),
        new("Теплоутилизатор",
        ["Тип / модель","Мощность, кВт","Приток ΔP, Па","Приток t до, °C","Приток t после, °C","Приток φ до, %","Приток φ после, %","Вытяжка ΔP, Па","Вытяжка t до, °C","Вытяжка t после, °C","Вытяжка φ до, %","Вытяжка φ после, %"]),
        new("Воздушно-тепловые завесы",
        ["Тип","Количество","Мощность, кВт","Расход, м³/ч","Теплоноситель"]),
        new("Пылегазоулавливающие устройства",
        ["Наименование","№","Количество, шт","Расход, м³/ч","Подсос, %","Сопротивление, Па"])
    ];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static List<VentEquipment> NewEquipment() =>
        Sections.Select(s => new VentEquipment
        {
            Project = Enumerable.Repeat("", s.Parameters.Length).ToList(),
            Actual = Enumerable.Repeat("", s.Parameters.Length).ToList()
        }).ToList();

    public static VentPassport Create(long objectId, string objectName, string address) => new()
    {
        ObjectId = objectId, Object = objectName, Address = address
    };

    public static string DetectPurpose(string? designation)
    {
        var system = (designation ?? "").Trim().ToUpperInvariant();
        if (system.StartsWith("ДП", StringComparison.Ordinal) ||
            system.StartsWith("ПД", StringComparison.Ordinal)) return "Система подпора";
        if (new[] { "ДВ", "ДУ", "ВД" }.Any(p => system.StartsWith(p, StringComparison.Ordinal)))
            return "Система дымоудаления";
        if (system.StartsWith('П')) return "Система приточной вентиляции";
        if (system.StartsWith('В')) return "Система вытяжной вентиляции";
        return "";
    }

    /// <summary>Blank must stay null. Invalid/negative/NaN/Infinity is rejected, never coerced.</summary>
    public static decimal? Flow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = text.Trim().Replace(' ', '\0').Replace('\u00A0', '\0');
        if (clean.Contains('\0')) throw new ArgumentException("Расход воздуха должен быть одним числом.");
        clean = clean.Replace(',', '.');
        if (!decimal.TryParse(clean, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint |
            NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new ArgumentException("Расход воздуха должен быть конечным неотрицательным числом.");
        return value;
    }

    public static (decimal? Project, decimal? Actual) Totals(IEnumerable<VentFlowPoint> points)
    {
        var items = points.ToArray();
        if (items.Length == 0) return (null, null);
        decimal? Total(Func<VentFlowPoint, string> selector)
        {
            decimal total = 0;
            foreach (var point in items)
            {
                var value = Flow(selector(point));
                if (value is null) return null;
                try { checked { total += value.Value; } }
                catch (OverflowException) { throw new ArgumentException("Сумма расхода слишком велика."); }
            }
            return total;
        }
        // Validate BOTH columns even when the first one contains blanks.
        foreach (var p in items) { Flow(p.Project); Flow(p.Actual); }
        return (Total(p => p.Project), Total(p => p.Actual));
    }

    public static string Format(decimal? value) =>
        value is null ? "" : value.Value.ToString("0.############################", CultureInfo.InvariantCulture).Replace('.', ',');

    public static bool IsDraft(VentPassport passport)
    {
        var flows = Totals(passport.Points);
        return passport.Points.Count == 0 || flows.Project is null || flows.Actual is null;
    }

    public static List<VentEquipment> DeepCopyEquipment(IEnumerable<VentEquipment> equipment) =>
        equipment.Select(e => new VentEquipment
        {
            Project = e.Project.ToList(), Actual = e.Actual.ToList(), Notes = e.Notes
        }).ToList();

    public static VentPassport Duplicate(VentPassport original)
    {
        var copy = JsonSerializer.Deserialize<VentPassport>(JsonSerializer.Serialize(original, Json), Json)!;
        copy.Id = Guid.NewGuid();
        copy.Revision = 0;
        copy.System += " (копия)";
        foreach (var point in copy.Points) point.Actual = "";
        return copy;
    }

    public static IReadOnlyList<VentPassport> BatchCreate(
        VentPassport template, string multiLine)
    {
        var result = new List<VentPassport>();
        foreach (var line in (multiLine ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var designation = line.Trim();
            if (designation.Length == 0) continue;
            var copy = Create(template.ObjectId, template.Object, template.Address);
            copy.Organization = template.Organization;
            copy.Zone = template.Zone;
            copy.Location = template.Location;
            copy.Date = template.Date;
            copy.Filter = template.Filter;
            copy.Equipment = DeepCopyEquipment(template.Equipment);
            copy.System = designation;
            copy.Purpose = DetectPurpose(designation);
            result.Add(copy);
        }
        return result;
    }

    public static string CatalogueKey(VentPassport p) =>
        string.Join(" / ", new[] { p.Equipment[0].Project[0], p.Equipment[0].Actual[0] }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));

    /// <summary>Catalog snapshot cannot carry system-specific E.1.1 air-flow.</summary>
    public static VentEquipmentTemplate MakeTemplate(VentPassport p)
    {
        var snapshot = DeepCopyEquipment(p.Equipment);
        snapshot[0].Project[3] = "";
        snapshot[0].Actual[3] = "";
        return new VentEquipmentTemplate
        {
            Key = CatalogueKey(p),
            ProjectModel = p.Equipment[0].Project[0],
            ActualModel = p.Equipment[0].Actual[0],
            Equipment = snapshot
        };
    }

    public static void ApplyTemplate(VentPassport passport, VentEquipmentTemplate template)
    {
        passport.Equipment = DeepCopyEquipment(template.Equipment);
        passport.Equipment[0].Project[3] = "";
        passport.Equipment[0].Actual[3] = "";
        passport.Filter = !string.IsNullOrWhiteSpace(passport.Equipment[7].Project[0]) ||
                          !string.IsNullOrWhiteSpace(passport.Equipment[7].Actual[0]);
    }

    public static void ApplyCommon(VentPassport source, IEnumerable<VentPassport> batch)
    {
        foreach (var p in batch)
        {
            p.Object = source.Object; p.Address = source.Address;
            p.Organization = source.Organization; p.Zone = source.Zone;
            p.Location = source.Location; p.Date = source.Date;
        }
    }

    public static void Validate(VentPassport p)
    {
        if (p.ObjectId <= 0) throw new ArgumentException("Паспорт должен принадлежать объекту.");
        if (p.System.Length > 160 || p.Object.Length > 500 ||
            p.Address.Length > 1000 || p.Organization.Length > 500 ||
            p.Zone.Length > 300 || p.Location.Length > 500 ||
            p.Purpose.Length > 500 || p.Date.Length > 80)
            throw new ArgumentException("Слишком длинное поле паспорта.");
        if (p.Equipment.Count != Sections.Length)
            throw new ArgumentException("В паспорте должны быть все восемь таблиц оборудования.");
        for (int i = 0; i < Sections.Length; i++)
        {
            var e = p.Equipment[i];
            if (e.Project.Count != Sections[i].Parameters.Length ||
                e.Actual.Count != Sections[i].Parameters.Length)
                throw new ArgumentException($"Неверные параметры таблицы Е.1.{i + 1}.");
            if (e.Project.Concat(e.Actual).Any(x => x is null || x.Length > 2000) ||
                e.Notes.Length > 4000) throw new ArgumentException("Слишком длинное описание оборудования.");
        }
        if (p.Points.Count > 10000) throw new ArgumentException("Слишком много замеров.");
        foreach (var point in p.Points)
        {
            if (point.Section.Length > 250 || point.Room.Length > 500)
                throw new ArgumentException("Слишком длинный участок или помещение.");
        }
        Totals(p.Points); // validates both numeric columns without inventing data
    }
}
