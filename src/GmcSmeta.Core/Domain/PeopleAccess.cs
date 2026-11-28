using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Domain;

/// <summary>Общий справочник Python people.py, без SQLite и персональных тестовых фикстур.</summary>
public sealed record PeoplePerson(long Id, string Fio, string Post, string Birth, string Note,
    string FioGen, string PostGen, bool Active, int SortOrder)
{
    public bool Selected { get; set; }
    public string DisplayGenitive => string.IsNullOrWhiteSpace(FioGen) ? PeopleRules.FioGenitive(Fio) : FioGen;
    public string DisplayPostGenitive => string.IsNullOrWhiteSpace(PostGen) ? PeopleRules.PostGenitive(Post) : PostGen;
}
public sealed record PeopleCar(long Id, string Model, string Plate, string Note, bool Active, int SortOrder)
{
    public bool Selected { get; set; }
}
public sealed record PeopleImportRow(string Fio, string Post = "", string Birth = "");
public sealed record CarImportRow(string Model, string Plate);
public sealed record PeopleImportPreview(IReadOnlyList<PeopleImportRow> People, IReadOnlyList<CarImportRow> Cars);
public sealed record PeopleImportResult(int NewPeople, int ExistingPeople, int NewCars, int ExistingCars);
public sealed record AccessHistory(long Id, long ObjectId, string Kind, DateTime CreatedAt,
    string File, string User, string Data);
public sealed record AccessPerson(long Id, string Fio, string Post, string Birth);
public sealed record AccessCar(long Id, string Model, string Plate);
public sealed record AccessSnapshot(IReadOnlyList<AccessPerson> People, IReadOnlyList<AccessCar> Cars,
    string Intro, string Requisites, long? CompanyId, string Company, string Signer, Guid OperationId);
public sealed record OrderSnapshot(long PersonId, string Person, IReadOnlyList<string> Files,
    string Number, Guid OperationId);
public sealed record AccessCompany(long Id, string Name, string Address, string InnKpp,
    string SignerPost, string SignerName);
public sealed record AccessSite(long Id, string Name, string Address, string SiteFull,
    string WorkTitle, string Folder, string Customer, string ContractNumber, string ContractDate)
{
    // Only populated for order-specific legal context; ordinary access lists
    // and historic object/technical documents retain their object scope.
    public bool LegalAgreementRequired { get; init; }
    public long? AgreementId { get; init; }
    public long? AgreementCompanyId { get; init; }
}

public static class PeopleRules
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex PlateSpaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly char[] Hush = "кгхжшчщ".ToCharArray();
    private const string Consonants = "бвгджзклмнпрстфхцчшщ";
    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля",
         "августа", "сентября", "октября", "ноября", "декабря"];

    public static string Clean(string? value) => Spaces.Replace(value ?? "", " ").Trim();
    public static string PersonKey(string? fio) => Clean(fio).ToUpperInvariant().Replace('Ё', 'Е');
    public static string PlateKey(string? plate)
    {
        var key = PlateSpaces.Replace(plate ?? "", "").ToUpperInvariant();
        const string latin = "ABEKMHOPCTYX";
        const string cyrillic = "АВЕКМНОРСТУХ";
        var b = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            var i = latin.IndexOf(c);
            b.Append(i >= 0 ? cyrillic[i] : c);
        }
        return b.ToString();
    }
    public static string BirthNorm(string? input)
    {
        var value = Clean(input);
        if (value.Length == 0) return "";
        // Match Python's explicit two-digit-year pivot, not the different .NET Calendar pivot.
        var match = Regex.Match(value, @"^(\d{1,2})[.,/\- ](\d{1,2})[.,/\- ](\d{2}|\d{4})$");
        if (!match.Success)
            match = Regex.Match(value, @"^(\d{2})(\d{2})(\d{4})$");
        if (!match.Success) return value;
        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        if (year < 100) year += year > DateTime.Today.Year % 100 ? 1900 : 2000;
        try { return new DateTime(year, month, day).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return value; }
    }

    private static bool Female(string fio)
    {
        var w = Clean(fio).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length >= 3)
            return new[] { "вна", "чна", "кызы", "кизи" }.Any(x => w[2].EndsWith(x, StringComparison.OrdinalIgnoreCase));
        return w.Length > 0 && new[] { "ова", "ева", "ина", "ая", "ына" }
            .Any(x => w[0].EndsWith(x, StringComparison.OrdinalIgnoreCase));
    }
    private static string Surname(string w, bool female)
    {
        var l = w.ToLowerInvariant();
        if (female)
        {
            if (l.EndsWith("ая")) return w[..^2] + "ой";
            if (new[] { "ова", "ева", "ина", "ына", "ёва" }.Any(l.EndsWith))
                return w[..^1] + "ой";
            return w;
        }
        if (new[] { "ый", "ой", "кий", "гий", "хий" }.Any(l.EndsWith))
            return w[..^2] + "ого";
        if (new[] { "ний", "жий", "чий", "ший", "щий" }.Any(l.EndsWith))
            return w[..^2] + "его";
        if (l.EndsWith("ий") || l.EndsWith("й") || l.EndsWith("ь"))
            return w[..^1] + "я";
        if (new[] { "ов", "ев", "ёв", "ин", "ын" }.Any(l.EndsWith))
            return w + "а";
        if (l.Length > 2 && l.EndsWith("а"))
            return w[..^1] + (Hush.Contains(l[^2]) ? "и" : "ы");
        if (l.Length > 2 && l.EndsWith("я")) return w[..^1] + "и";
        if (l.Length > 0 && Consonants.Contains(l[^1])) return w + "а";
        return w;
    }
    private static string FirstName(string w, bool female)
    {
        var l = w.ToLowerInvariant();
        if (l.EndsWith("ия") || l.EndsWith("я")) return w[..^1] + "и";
        if (l.EndsWith("а")) return w[..^1] + (l.Length > 1 && Hush.Contains(l[^2]) ? "и" : "ы");
        if (female) return w;
        if (l.EndsWith("й") || l.EndsWith("ь")) return w[..^1] + "я";
        if (l.Length > 0 && Consonants.Contains(l[^1])) return w + "а";
        return w;
    }
    private static string Patronymic(string w)
    {
        var l = w.ToLowerInvariant();
        return l.EndsWith("ич") ? w + "а" : l.EndsWith("на") ? w[..^1] + "ы" : w;
    }
    public static string FioGenitive(string? fio)
    {
        var w = Clean(fio).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length == 0) return "";
        var female = Female(fio ?? "");
        var result = new List<string> { Surname(w[0], female) };
        if (w.Length > 1) result.Add(FirstName(w[1], female));
        if (w.Length > 2) result.Add(Patronymic(w[2]));
        result.AddRange(w.Skip(3));
        return string.Join(" ", result);
    }
    private static string GenWord(string w)
    {
        var l = w.ToLowerInvariant();
        if (new[] { "ый", "ий", "ой" }.Any(l.EndsWith))
            return w[..^2] + (l.EndsWith("ний") && l != "главный" ? "его" : "ого");
        if (l.EndsWith("ель") || l.EndsWith("ь") || l.EndsWith("й")) return w[..^1] + "я";
        if (l.EndsWith("а") && l.Length > 1)
            return w[..^1] + (Hush.Contains(l[^2]) ? "и" : "ы");
        if (l.Length > 0 && Consonants.Contains(l[^1])) return w + "а";
        return w;
    }
    public static string PostGenitive(string? post)
    {
        bool done = false;
        var output = new List<string>();
        foreach (var w in Clean(post).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = w.ToLowerInvariant().TrimEnd('.');
            if (done || w.EndsWith('.') || !Regex.IsMatch(l, @"^[а-яё\-]+$"))
            {
                output.Add(w);
                continue;
            }
            output.Add(GenWord(w));
            if (!new[] { "ый", "ий", "ой", "ая" }.Any(l.EndsWith)) done = true;
        }
        return string.Join(" ", output);
    }
    public static string ShortFio(string? fio)
    {
        var w = Clean(fio).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return w.Length == 0 ? "" : w[0] + (w.Length == 1 ? "" :
            " " + string.Concat(w.Skip(1).Take(2).Select(x => char.ToUpperInvariant(x[0]) + ".")));
    }
    public static string NextNumber(string number) =>
        Regex.Replace(number, @"(\d+)(\D*)$", m =>
        {
            var n = m.Groups[1].Value;
            if (!long.TryParse(n, out var value) || value == long.MaxValue)
                throw new ArgumentException("Номер приказа превышает допустимый диапазон.");
            return (value + 1).ToString(CultureInfo.InvariantCulture).PadLeft(n.Length, '0') + m.Groups[2].Value;
        });
    public static string DateLong(DateTime day) => "«" +
        day.ToString("dd", CultureInfo.InvariantCulture) + "» " +
        Months[day.Month - 1] + " " + day.Year + " г.";
    public static int PostRank(string? post)
    {
        var l = (post ?? "").ToLowerInvariant();
        var ranks = new (string Key, int Rank)[]
        {
            ("генеральн", 0), ("ген.", 0), ("директор", 1), ("главн", 2),
            ("начальник", 3), ("производител", 4), ("прораб", 4), ("мастер", 5),
            ("инженер", 6), ("пто", 6), ("снабж", 7), ("кладовщ", 8)
        };
        foreach (var (key, rank) in ranks)
        {
            if (l.Contains(key)) return rank;
        }
        return 20;
    }
    public static IReadOnlyList<PeoplePerson> SortPeople(IEnumerable<PeoplePerson> people) =>
        people.OrderBy(x => x.SortOrder == 0 ? int.MaxValue : x.SortOrder)
            .ThenBy(x => PostRank(x.Post)).ThenBy(x => x.Post)
            .ThenBy(x => x.Fio).ToArray();
    public static string DefaultIntro(AccessSite site)
    {
        var work = string.IsNullOrWhiteSpace(site.WorkTitle) ? "Монтаж системы вентиляции" : Clean(site.WorkTitle);
        var first = work.Split(' ', 2);
        var lower = first[0].ToLowerInvariant();
        if (new[] { "аж", "ет", "ок", "ор" }.Any(lower.EndsWith)) lower += "у";
        else if (lower.EndsWith("ка")) lower = lower[..^1] + "е";
        var phrase = lower + (first.Length > 1 ? " " + first[1] : "");
        var place = Clean(string.IsNullOrWhiteSpace(site.SiteFull) ? 
            string.IsNullOrWhiteSpace(site.Address) ? site.Name : site.Address : site.SiteFull).Trim('«', '»', '"', ' ');
        return "Для выполнения работ по " + phrase + " на объекте: «" + place + "»";
    }
    public static string SafeFileName(string value)
    {
        var cleaned = Regex.Replace(value, @"[\\/:*?""<>|]+", " ").Trim().TrimEnd('.');
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}