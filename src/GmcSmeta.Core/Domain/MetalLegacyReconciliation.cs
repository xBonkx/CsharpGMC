using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// An immutable row of Python's dbo.metal_entries. Area and reservation are milli-m²;
/// CreatedAt is the original SQL datetime (local/unspecified), NOT a fabricated UTC time.
/// </summary>
public sealed record MetalLegacyEntry(
    long Id, string Category, string Material, string Kind, long Area, int Pieces,
    long Reservation, string Source, string Destination, long? ObjectId,
    string ObjectName, long? LotId, string Supplier, string Invoice, string Note,
    string User, DateTime CreatedAt);

public sealed record MetalLegacyPlace(string Category, string Material, string Place,
    long Area, long Pieces, long Reserved);
public sealed record MetalLegacyObject(string Category, string Material, long? ObjectId,
    string ObjectName, long Demand, long Issued, long Reserved);
public sealed record MetalLegacyLot(long Id, string Category, string Material,
    string Supplier, string Invoice, long Ordered, int OrderedPieces, bool Paid,
    long Received, int ReceivedPieces);
public sealed record MetalLegacyPreview(
    string SourceSha256, int EventCount, DateTime? FirstDate, DateTime? LastDate,
    IReadOnlyDictionary<string, int> EventCounts,
    IReadOnlyList<MetalLegacyPlace> Places, IReadOnlyList<MetalLegacyObject> Objects,
    IReadOnlyList<MetalLegacyLot> Lots, IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings)
{
    public bool CanImport => EventCount > 0 && Errors.Count == 0;
    public long Physical => Places.Where(x => x.Place != MetalWarehouseRules.SupplierPlace).Sum(x => x.Area);
    public long Supplier => Places.Where(x => x.Place == MetalWarehouseRules.SupplierPlace).Sum(x => x.Area);
    public long Reserved => Places.Sum(x => x.Reserved);
}

/// <summary>Read-only, deterministic ledger replay. Never adjusts unexplained discrepancies.</summary>
public static class MetalLegacyReconciliation
{
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
    {
        "Начальный запас", "Приход", "Перемещение", "Потребность", "Выдача",
        "Возврат", "Списание", "Корректировка", "Резерв", "Снять резерв",
        "Партия", "Оплата партии", "Получение партии"
    };

    public static string Hash(IReadOnlyList<MetalLegacyEntry> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // JSON includes every source column and precise legacy IDs, never environment-dependent hashes.
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source.OrderBy(e => e.Id).ToArray()));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static MetalLegacyPreview Preview(IReadOnlyList<MetalLegacyEntry> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var errors = new List<string>();
        var warnings = new List<string>();
        var rows = source.OrderBy(e => e.Id).ToArray();
        if (rows.Length == 0) errors.Add("В metal_entries нет исторических событий.");
        if (rows.Length > 100_000) errors.Add("История больше 100000 событий: требуется отдельная пакетная приёмка.");
        var ids = new HashSet<long>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var places = new Dictionary<(string Category, string Material, string Place), (long Area, long Pieces, long Reserved)>();
        var objects = new Dictionary<(string Category, string Material, long? Id, string Name), (long Demand, long Issued, long Reserved)>();
        var lots = new Dictionary<long, (MetalLegacyEntry Header, bool Paid, long Received, int ReceivedPieces)>();
        var invoices = new HashSet<(string, string, string)>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var reservationByObject = new Dictionary<(string Category, string Material, string Place, long? Id, string Name), long>();

        void Error(MetalLegacyEntry e, string message) => errors.Add($"metal_entries.id={e.Id}: {message}");
        void AddPlace(MetalLegacyEntry e, string place, long area, long pieces, long reserved)
        {
            var key = (e.Category, e.Material, place);
            var value = places.GetValueOrDefault(key);
            places[key] = (checked(value.Area + area), checked(value.Pieces + pieces), checked(value.Reserved + reserved));
        }
        void AddObject(MetalLegacyEntry e, long demand, long issued, long reserved)
        {
            if (e.ObjectId is null && string.IsNullOrWhiteSpace(e.ObjectName)) return;
            var key = (e.Category, e.Material, e.ObjectId, e.ObjectId is null ? e.ObjectName : "");
            var value = objects.GetValueOrDefault(key);
            objects[key] = (checked(value.Demand + demand), checked(value.Issued + issued), checked(value.Reserved + reserved));
        }
        foreach (var e in rows)
        {
            if (e.Id <= 0 || !ids.Add(e.Id)) Error(e, "Повторный или недопустимый исторический ID.");
            if (!signatures.Add(JsonSerializer.Serialize(e with { Id = 0, CreatedAt = default })))
                warnings.Add($"metal_entries.id={e.Id}: повторяющийся payload при разных ID — проверьте источник.");
            if (!Kinds.Contains(e.Kind)) { Error(e, "Неизвестный вид движения: " + e.Kind); continue; }
            counts[e.Kind] = counts.GetValueOrDefault(e.Kind) + 1;
            try { MetalWarehouseRules.CheckMaterial(e.Category, e.Material); }
            catch (ArgumentException ex) { Error(e, ex.Message); continue; }
            if (e.Pieces is < 0 or > 10_000_000 || e.Category != "Бухты" && e.Pieces != 0)
                Error(e, "Несоответствие единиц: штуки допустимы только для бухт.");
            if (e.CreatedAt == default) Error(e, "Нет исходной даты.");
            if (e.CreatedAt.Kind != DateTimeKind.Unspecified)
                Error(e, "Историческая дата должна храниться без неявного смещения часового пояса.");
            if (e.Source.Length > 0 && !MetalWarehouseRules.Places.Contains(e.Source) ||
                e.Destination.Length > 0 && !MetalWarehouseRules.Places.Contains(e.Destination) ||
                e.Source.Length > 0 && e.Source == e.Destination)
                Error(e, "Неизвестное или одинаковое место хранения.");
            if (e.Area < 0 && e.Kind != "Потребность")
                Error(e, "Отрицательное проведённое движение; требуется исторический разбор.");
            if (e.Kind == "Корректировка" && string.IsNullOrWhiteSpace(e.Note))
                Error(e, "Корректировка без причины.");
            if (e.Kind is "Потребность" or "Резерв" or "Снять резерв" && e.Pieces != 0)
                Error(e, "Штуки в потребности/резерве не являются физическим движением.");
            var (needsSource, needsTarget) = e.Kind switch
            {
                "Начальный запас" or "Приход" or "Возврат" => (false, true),
                "Перемещение" or "Получение партии" => (true, true),
                "Выдача" or "Списание" or "Резерв" or "Снять резерв" => (true, false),
                "Потребность" or "Партия" => (false, false),
                _ => ((bool?)null, (bool?)null)
            };
            if (needsSource is bool expectedSource &&
                (expectedSource != (e.Source.Length > 0) || needsTarget != (e.Destination.Length > 0)))
                Error(e, "Отсутствует обязательное место движения или есть лишняя проводка.");
            if (e.Kind == "Корректировка" && (e.Source.Length == 0) == (e.Destination.Length == 0))
                Error(e, "Корректировка требует ровно одно место.");
            if (e.Kind is "Выдача" or "Возврат" or "Резерв" or "Снять резерв" && e.ObjectId is null)
                Error(e, "Историческая операция требует идентификатор объекта.");
            if (e.Kind is "Резерв" or "Снять резерв" && e.Area != 0)
                Error(e, "Резерв не должен менять физическую площадь.");
            if (e.Kind is "Резерв" && e.Reservation <= 0 ||
                e.Kind is "Снять резерв" && e.Reservation >= 0 ||
                e.Kind is not ("Резерв" or "Снять резерв" or "Выдача") && e.Reservation != 0 ||
                e.Kind == "Выдача" && e.Reservation > 0)
                Error(e, "Неверный знак/тип резерва.");
            if (e.Kind is "Выдача" or "Резерв" or "Снять резерв" && e.Source == MetalWarehouseRules.SupplierPlace ||
                e.Kind == "Возврат" && e.Destination == MetalWarehouseRules.SupplierPlace)
                Error(e, "Выдача/резерв/возврат из хранения поставщика недопустимы.");

            if (e.Kind == "Партия")
            {
                if (e.LotId is not null || e.Category != "Бухты" || e.Area <= 0 || e.Pieces <= 0 ||
                    string.IsNullOrWhiteSpace(e.Supplier) || string.IsNullOrWhiteSpace(e.Invoice) ||
                    e.Source.Length > 0 || e.Destination.Length > 0)
                    Error(e, "Некорректный заголовок партии.");
                if (!invoices.Add((e.Category + "/" + e.Material, e.Supplier, e.Invoice)))
                    Error(e, "Дублирующий счёт партии.");
                lots[e.Id] = (e, false, 0, 0);
            }
            else if (e.Kind is "Оплата партии" or "Получение партии")
            {
                if (e.LotId is not long lotKey || !lots.TryGetValue(lotKey, out var lot))
                {
                    Error(e, "Не найдена предшествующая партия для оплаты/получения.");
                    continue;
                }
                if (lot.Header.Category != e.Category || lot.Header.Material != e.Material ||
                    lot.Header.Supplier != e.Supplier || lot.Header.Invoice != e.Invoice)
                    Error(e, "Партия и движение имеют разные реквизиты/материал.");
                if (e.Kind == "Оплата партии")
                {
                    if (lot.Paid || e.Area != lot.Header.Area || e.Pieces != lot.Header.Pieces ||
                        e.Destination != MetalWarehouseRules.SupplierPlace || e.Source.Length > 0)
                        Error(e, "Повторная/частичная оплата без отдельного события или неверные реквизиты.");
                    lot.Paid = true;
                }
                else
                {
                    if (!lot.Paid || e.Area <= 0 || e.Pieces <= 0 ||
                        e.Source != MetalWarehouseRules.SupplierPlace ||
                        e.Destination.Length == 0 || e.Destination == MetalWarehouseRules.SupplierPlace)
                        Error(e, "Получение до оплаты или некорректное место/количество.");
                    lot.Received = checked(lot.Received + e.Area);
                    lot.ReceivedPieces = checked(lot.ReceivedPieces + e.Pieces);
                    if (lot.Received > lot.Header.Area || lot.ReceivedPieces > lot.Header.Pieces ||
                        (lot.Received == lot.Header.Area) != (lot.ReceivedPieces == lot.Header.Pieces))
                        Error(e, "Получение превышает оплаченные площадь/бухты.");
                }
                lots[lotKey] = lot;
            }
            else if (e.LotId is not null)
                Error(e, "Неожиданная привязка к партии.");

            // Python balances(): every source/target leg affects stock; reservation rows
            // have area 0, while demand and issues are separately accumulated by object.
            if (e.Source.Length > 0)
            {
                AddPlace(e, e.Source, -e.Area, e.Kind is "Резерв" or "Снять резерв" ? 0 : -e.Pieces,
                    e.Reservation);
                var location = places[(e.Category, e.Material, e.Source)];
                if (location.Area < 0 || location.Pieces < 0)
                    Error(e, "Промежуточный отрицательный запас в источнике.");
            }
            if (e.Destination.Length > 0)
                AddPlace(e, e.Destination, e.Area, e.Pieces, 0);
            if (e.Kind == "Потребность") AddObject(e, e.Area, 0, 0);
            if (e.Kind == "Выдача") AddObject(e, 0, e.Area, e.Reservation);
            if (e.Kind == "Возврат") AddObject(e, 0, -e.Area, 0);
            if (e.Kind is "Резерв" or "Снять резерв") AddObject(e, 0, 0, e.Reservation);
            if (e.Reservation != 0)
            {
                var key = (e.Category, e.Material, e.Source, e.ObjectId, e.ObjectId is null ? e.ObjectName : "");
                var newValue = checked(reservationByObject.GetValueOrDefault(key) + e.Reservation);
                reservationByObject[key] = newValue;
                if (newValue < 0) Error(e, "Резерв объекта стал отрицательным.");
            }
        }
        foreach (var p in places)
        {
            if (p.Value.Area < 0 || p.Value.Pieces < 0 || p.Value.Reserved < 0 ||
                p.Value.Reserved > p.Value.Area && p.Key.Place != MetalWarehouseRules.SupplierPlace)
                errors.Add($"Отрицательный/невозможный итог по месту {p.Key.Category}/{p.Key.Material}/{p.Key.Place}.");
        }
        foreach (var obj in objects)
            if (obj.Value.Demand < 0 || obj.Value.Issued < 0 || obj.Value.Reserved < 0)
                errors.Add($"Отрицательная потребность/выдача/резерв по объекту {obj.Key.Id} {obj.Key.Name}.");
        foreach (var lot in lots.Values)
        {
            if (!lot.Paid) warnings.Add($"Партия #{lot.Header.Id}: счёт не оплачен; у поставщика не учитывать.");
            if (lot.Paid && lot.Received < lot.Header.Area)
                warnings.Add($"Партия #{lot.Header.Id}: оплачено, частично не получено.");
        }
        var byPlace = places.Select(p => new MetalLegacyPlace(p.Key.Category, p.Key.Material,
            p.Key.Place, p.Value.Area, p.Value.Pieces, p.Value.Reserved))
            .OrderBy(p => p.Category).ThenBy(p => p.Material).ThenBy(p => p.Place).ToArray();
        var byObject = objects.Select(p => new MetalLegacyObject(p.Key.Category, p.Key.Material,
            p.Key.Id, p.Key.Name, p.Value.Demand, p.Value.Issued, p.Value.Reserved)).ToArray();
        var byLot = lots.Select(p => new MetalLegacyLot(p.Key, p.Value.Header.Category,
            p.Value.Header.Material, p.Value.Header.Supplier, p.Value.Header.Invoice,
            p.Value.Header.Area, p.Value.Header.Pieces, p.Value.Paid,
            p.Value.Received, p.Value.ReceivedPieces)).OrderBy(p => p.Id).ToArray();
        return new MetalLegacyPreview(Hash(rows), rows.Length,
            rows.Length == 0 ? null : rows.Min(e => e.CreatedAt),
            rows.Length == 0 ? null : rows.Max(e => e.CreatedAt),
            counts, byPlace, byObject, byLot, errors, warnings);
    }
}
