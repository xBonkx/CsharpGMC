using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>Metal quantities are signed thousandths of a square metre, never floating point.</summary>
public sealed record MetalMovement(
    long Id, long MaterialId, string Category, string Material, string Kind, long Area,
    int Pieces, long Reservation, string Source, string Destination,
    long? ObjectId, string ObjectName, string User, string Note,
    long? LotId = null, string Supplier = "", string Invoice = "", DateTime CreatedUtc = default);

public sealed record MetalOpening(
    string Category, string Material,
    IReadOnlyList<(string Place, decimal Area)> Stock,
    IReadOnlyList<(string Object, decimal Area)> Demand,
    decimal? CachedBalance,
    IReadOnlyList<(string Place, decimal Pieces)> Pieces);

public sealed record MetalOperation(
    string Category, string Material, string Kind, decimal Area,
    int Pieces = 0, string Source = "", string Destination = "",
    long? ObjectId = null, string ObjectName = "", string Note = "",
    Guid? OperationId = null);

public sealed record MetalPrepared(long Area, int Pieces, long Reservation,
    string Source, string Destination);

public static class MetalWarehouseRules
{
    public const string SupplierPlace = "У поставщиков";
    public static readonly string[] Places =
        ["Станки", "В наличии", "Склад", "Цех Каплунова", "Цех Выселковая", SupplierPlace];
    public static readonly string[] Categories = ["Бухты", "Штрипсы", "Листы"];
    public static readonly string[] Operations =
        ["Приход", "Перемещение", "Потребность", "Выдача", "Возврат", "Списание", "Корректировка", "Резерв", "Снять резерв"];

    public static long Units(decimal area)
    {
        if (decimal.Abs(area) > 1_000_000_000m)
            throw new ArgumentOutOfRangeException(nameof(area), "Укажите корректную площадь в м².");
        return checked((long)decimal.Round(area * 1000m, 0, MidpointRounding.AwayFromZero));
    }

    public static int Pieces(decimal value)
    {
        if (value != decimal.Truncate(value) || value < 0 || value > 10_000_000)
            throw new ArgumentException("Количество бухт должно быть целым неотрицательным числом.");
        return (int)value;
    }

    public static decimal Sqm(long area) => area / 1000m;
    public static string SqmText(long area) => Sqm(area).ToString("0.###", CultureInfo.GetCultureInfo("ru-RU"));

    public static void ValidateOpening(IReadOnlyList<MetalOpening> records)
    {
        if (records.Count == 0) throw new ArgumentException("В файле нет начальных остатков.");
        foreach (var r in records)
        {
            CheckMaterial(r.Category, r.Material);
            foreach (var (place, quantity) in r.Stock)
            {
                CheckPlace(place);
                if (Units(quantity) < 0)
                    throw new ArgumentException($"Отрицательный начальный запас: {r.Category}, {r.Material}, {place}.");
            }
            foreach (var (site, quantity) in r.Demand)
                if (string.IsNullOrWhiteSpace(site) || Units(quantity) < 0)
                    throw new ArgumentException($"Отрицательная потребность: {site}, {r.Material}.");
            foreach (var (place, count) in r.Pieces)
            {
                CheckPlace(place);
                if (r.Category != "Бухты" && count != 0)
                    throw new ArgumentException("Штуки допустимы только для бухт.");
                Pieces(count);
            }
        }
    }

    public static void CheckMaterial(string category, string material)
    {
        if (!Categories.Contains(category) || string.IsNullOrWhiteSpace(material) || material.Length > 80)
            throw new ArgumentException("Укажите категорию и материал (не более 80 символов).");
    }

    private static void CheckPlace(string place)
    {
        if (!Places.Contains(place)) throw new ArgumentException("Неизвестное место хранения: " + place);
    }

    public static MetalPrepared Prepare(MetalOperation op, IReadOnlyList<MetalMovement> entries,
        bool objectExists)
    {
        CheckMaterial(op.Category, op.Material);
        if (!Operations.Contains(op.Kind)) throw new ArgumentException("Выберите складскую операцию.");
        if (op.Pieces < 0 || op.Pieces > 10_000_000 ||
            op.Pieces != 0 && (op.Category != "Бухты" ||
                op.Kind is "Резерв" or "Снять резерв" or "Потребность"))
            throw new ArgumentException("Штуки вводятся только при движении бухт.");
        if (!string.IsNullOrEmpty(op.Note) && op.Note.Length > 10000)
            throw new ArgumentException("Примечание слишком длинное.");
        long value = Units(op.Area);
        if (value == 0 && op.Kind != "Потребность" ||
            value < 0 && op.Kind != "Корректировка")
            throw new ArgumentException("Укажите ненулевую площадь; минус допустим при корректировке.");

        var source = op.Source ?? "";
        var target = op.Destination ?? "";
        if (op.Kind == "Корректировка")
        {
            if (string.IsNullOrWhiteSpace(op.Note))
                throw new ArgumentException("Для корректировки укажите причину.");
            if (value < 0) { source = target; target = ""; value = -value; }
        }
        var placeNeeds = op.Kind switch
        {
            "Приход" or "Возврат" => (false, true),
            "Перемещение" => (true, true),
            "Выдача" or "Списание" or "Резерв" or "Снять резерв" => (true, false),
            _ => ((bool?)null, (bool?)null)
        };
        if (placeNeeds.Item1 is bool needSource &&
            (string.IsNullOrEmpty(source) != !needSource || string.IsNullOrEmpty(target) != !placeNeeds.Item2!.Value))
            throw new ArgumentException("Укажите место списания или поступления для операции.");
        if (source.Length > 0 && !Places.Contains(source) ||
            target.Length > 0 && !Places.Contains(target) ||
            source.Length > 0 && source == target ||
            op.Kind == "Корректировка" && (string.IsNullOrEmpty(source) == string.IsNullOrEmpty(target)) ||
            op.Kind == "Потребность" && (source.Length > 0 || target.Length > 0))
            throw new ArgumentException("Проверьте места хранения.");

        if (op.Kind is "Выдача" or "Возврат" or "Резерв" &&
            (source == SupplierPlace || target == SupplierPlace))
            throw new ArgumentException("Сначала переместите металл с хранения поставщика к себе.");
        if (op.Kind is "Потребность" or "Выдача" or "Возврат" or "Резерв" or "Снять резерв" &&
            (!objectExists || op.ObjectId is null))
            throw new ArgumentException("Выберите объект из справочника.");
        if (entries.Count == 0) throw new ArgumentException("Материал не найден в складском журнале.");

        long Stock(string place) => entries.Sum(e =>
            (e.Destination == place ? e.Area : 0) - (e.Source == place && e.Kind is not ("Резерв" or "Снять резерв") ? e.Area : 0));
        int Count(string place) => entries.Sum(e =>
            (e.Destination == place ? e.Pieces : 0) - (e.Source == place && e.Kind is not ("Резерв" or "Снять резерв") ? e.Pieces : 0));
        // Reservation rows use Source to identify the warehouse but don't transfer stock.
        long Reserve(string place, long? objectId) =>
            entries.Where(e => e.Source == place && e.ObjectId == objectId).Sum(e => e.Reservation);

        if (op.Kind == "Потребность")
        {
            var current = entries.Where(e => e.Kind == "Потребность" &&
                e.ObjectId == op.ObjectId).Sum(e => e.Area);
            value -= current; // incoming area is the desired total, not a delta
            if (value == 0) throw new ArgumentException("Потребность уже имеет это значение.");
        }

        long reservation = 0;
        if (source.Length > 0)
        {
            var own = op.ObjectId is long ? Reserve(source, op.ObjectId) : 0;
            if (op.Kind == "Снять резерв")
            {
                if (value > own) throw new ArgumentException("У объекта нет такого резерва.");
                reservation = -value;
            }
            else
            {
                var held = entries.Where(e => e.Source == source).Sum(e => e.Reservation);
                // Paid supplier stock which has not yet been physically received is not free inventory.
                long lotArea = entries.Where(e => e.LotId is not null)
                    .Sum(e => e.Kind == "Оплата партии" ? e.Area :
                        e.Kind == "Получение партии" ? -e.Area : 0);
                int lotCount = entries.Where(e => e.LotId is not null)
                    .Sum(e => e.Kind == "Оплата партии" ? e.Pieces :
                        e.Kind == "Получение партии" ? -e.Pieces : 0);
                var sourceArea = Stock(source) - (source == SupplierPlace ? lotArea : 0);
                var free = sourceArea - held;
                var freePieces = Count(source) - (source == SupplierPlace ? lotCount : 0);
                if (op.Kind == "Выдача")
                {
                    free += own;
                    reservation = -Math.Min(own, value);
                }
                if (value > free || op.Pieces > freePieces)
                    throw new ArgumentException("В выбранном месте недостаточно свободного металла.");
                if (op.Kind == "Резерв") reservation = value;
                else if (op.Pieces != freePieces && value == sourceArea && freePieces != 0)
                    throw new ArgumentException("При списании всей площади укажите все оставшиеся бухты.");
            }
        }
        if (op.Kind == "Возврат")
        {
            var delivered = entries.Where(e => e.ObjectId == op.ObjectId)
                .Sum(e => e.Kind == "Выдача" ? e.Area : e.Kind == "Возврат" ? -e.Area : 0);
            var deliveredPieces = entries.Where(e => e.ObjectId == op.ObjectId)
                .Sum(e => e.Kind == "Выдача" ? e.Pieces : e.Kind == "Возврат" ? -e.Pieces : 0);
            if (delivered < value || deliveredPieces < op.Pieces)
                throw new ArgumentException("Возврат больше ранее выданного количества.");
        }
        if (op.Kind is "Резерв" or "Снять резерв") value = 0;
        return new MetalPrepared(value, op.Pieces, reservation, source, target);
    }
}
