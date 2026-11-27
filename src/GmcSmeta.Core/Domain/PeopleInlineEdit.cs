using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Edits one field on a detached copy; manual genitive values remain separate.</summary>
public static class PeopleInlineEdit
{
    public static string Read(object row, string field) => row switch
    {
        PeoplePerson p => field switch
        {
            nameof(p.Fio) => p.Fio, nameof(p.Post) => p.Post, nameof(p.Birth) => p.Birth,
            nameof(p.Note) => p.Note, nameof(p.FioGen) => p.FioGen, nameof(p.PostGen) => p.PostGen,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        },
        PeopleCar c => field switch
        {
            nameof(c.Model) => c.Model, nameof(c.Plate) => c.Plate, nameof(c.Note) => c.Note,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        },
        _ => throw new ArgumentException("Неизвестная строка справочника.")
    };

    public static object Change(object row, string field, string input)
    {
        var value = PeopleRules.Clean(input);
        if (row is PeoplePerson p)
        {
            return field switch
            {
                nameof(p.Fio) => p with { Fio = Checked(value, 255, "ФИО", required: true) },
                nameof(p.Post) => p with { Post = Checked(value, 128, "Должность") },
                nameof(p.Birth) => p with { Birth = Birth(value) },
                nameof(p.Note) => p with { Note = value },
                nameof(p.FioGen) => p with { FioGen = Checked(value, 255, "ФИО — кого") },
                nameof(p.PostGen) => p with { PostGen = Checked(value, 128, "Должность — кого") },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }
        if (row is PeopleCar c)
        {
            var changed = field switch
            {
                nameof(c.Model) => c with { Model = Checked(value, 128, "Марка") },
                nameof(c.Plate) => c with { Plate = Checked(value.ToUpperInvariant(), 32, "Госномер") },
                nameof(c.Note) => c with { Note = value },
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            if (changed.Model.Length == 0 && changed.Plate.Length == 0)
                throw new ArgumentException("Укажите марку или госномер.");
            return changed;
        }
        throw new ArgumentException("Неизвестная строка справочника.");
    }

    private static string Checked(string value, int max, string field, bool required = false)
    {
        if (required && value.Length == 0) throw new ArgumentException(field + ": заполните поле.");
        if (value.Length > max) throw new ArgumentException(field + ": максимум " + max + " символов.");
        return value;
    }

    private static string Birth(string value)
    {
        if (value.Length == 0) return "";
        var normalized = PeopleRules.BirthNorm(value);
        if (!DateTime.TryParseExact(normalized, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date.Date > DateTime.Today)
            throw new ArgumentException("Введите корректную дату рождения (дд.мм.гггг).");
        return normalized;
    }
}
