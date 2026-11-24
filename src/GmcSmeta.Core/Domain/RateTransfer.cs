using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using GmcSmeta.Core.Db;

namespace GmcSmeta.Core.Domain;

/// <summary>Индексы цен строки в RateRow.Prices (rate_transfer.FIELDS).</summary>
public static class RateFields
{
    public const int WorkCost = 0;
    public const int WorkSell = 1;
    public const int PurchasePrice = 2;
    public const int Price = 3;
    public static readonly int[] Work = { WorkCost, WorkSell };
    public static readonly int[] Material = { PurchasePrice, Price };
    public static readonly string[] Short = { "СМР закуп", "СМР клиенту", "Мат. закуп", "Мат. клиенту" };
}

/// <summary>Строка КП для переноса расценок (rate_transfer.RateRow).</summary>
public sealed record RateRow(int Id, int Lineage, string Name, string Num, string Unit,
    ImmutableArray<string> System, string Basis, string? ParentKey, string ParentName,
    int? CatalogId, decimal Qty, ImmutableArray<decimal?> Prices, string Formulas)
{
    public string Key => Catalog.Key(Name, Unit);

    public string SystemLabel => ParentName.Length > 0
        ? string.Join(" › ", System.Concat(new[] { ParentName }))
        : string.Join(" › ", System);

    public decimal? Rate(int field) => Prices[field];
}

/// <summary>КП целиком: строки в порядке id (rate_transfer.Document).</summary>
public sealed record RateDocument(int Id, string Title, int Root, (bool VatTop, decimal VatRate) Vat,
    decimal Smr, IReadOnlyList<RateRow> Rows);

/// <summary>Данные переноса: источник, приёмник и защищённые цены строк (rate_transfer.TransferData).</summary>
public sealed class TransferData
{
    public RateDocument Source { get; }
    public RateDocument Target { get; }
    public ImmutableHashSet<int> ProtectedPrices { get; }

    public TransferData(RateDocument source, RateDocument target, ImmutableHashSet<int> protectedPrices)
    {
        Source = source;
        Target = target;
        ProtectedPrices = protectedPrices;
    }

    private string? _canonical;
    private string Canonical => _canonical ??= JsonSerializer.Serialize(
        new { Source, Target, Protected = ProtectedPrices.OrderBy(x => x) }, JsonOptions);

    public override bool Equals(object? obj) => obj is TransferData other && other.Canonical == Canonical;
    public override int GetHashCode() => Canonical.GetHashCode();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}

/// <summary>Результат сопоставления одной строки (rate_transfer.TransferRow).</summary>
public sealed record TransferRow(RateRow Source, RateRow? Target,
    ImmutableArray<(int Field, decimal Amount)> Changes, string Status, bool Exact, bool ZeroReview = false);

/// <summary>Сопоставление строк и расчёт переноса расценок — перенос rate_transfer.py.</summary>
public static class RateTransfer
{
    /// <summary>«   »-нормализация: регистр и лишние пробелы не влияют (rate_transfer._norm).</summary>
    public static string Norm(string? text) =>
        string.Join(' ', ((text ?? "")).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Совместимость строк: верхняя ↔ верхняя, комплектующая ↔ комплектующая, единица без точки.</summary>
    public static bool Compatible(RateRow source, RateRow target) =>
        (source.ParentKey is null) == (target.ParentKey is null)
        && Norm(source.Unit).TrimEnd('.').Equals(Norm(target.Unit).TrimEnd('.'), StringComparison.Ordinal);

    private static bool IdentityCompatible(RateRow source, RateRow target) =>
        source.Basis.Length == 0 || target.Basis.Length == 0 || source.Basis == target.Basis;

    private static (string[] System, string? ParentKey, string Key) SystemKey(RateRow row) =>
        (row.System.Select(Norm).ToArray(), row.ParentKey, row.Key);

    /// <summary>Есть ли переносимая цена: у комплектующей только закупка; «Монтаж …» не переносит материалы; цена ≠ 0.</summary>
    private static bool HasRate(RateRow row, int field)
    {
        var value = row.Rate(field);
        return value is not null
            && (row.ParentKey is null || field == RateFields.PurchasePrice)
            && (!(field == RateFields.PurchasePrice || field == RateFields.Price) || !EstimateStatistics.IsWorkName(row.Name))
            && (field != RateFields.Price || value != 0m);
    }

    private static List<RateRow> ExactCandidates(RateRow row, IEnumerable<RateRow> candidates)
    {
        var list = candidates.Where(r => IdentityCompatible(row, r)).ToList();
        // Идентификатор каталога уточняет дубль, но часто выводится из имени и единицы.
        if (list.Count > 1 && row.CatalogId is not null)
        {
            var refined = list.Where(r => r.CatalogId == row.CatalogId).ToList();
            if (refined.Count > 0) return refined;
        }
        return list;
    }

    private static readonly string[] ReviewStatuses = { "Выбрать вручную", "Конфликт этапов", "Несколько источников" };

    public static bool NeedsReview(string status) =>
        ReviewStatuses.Contains(status) || status == "Не найдено";

    /// <summary>Сопоставление строк источника и приёмника (rate_transfer.make_preview).</summary>
    public static List<TransferRow> MakePreview(TransferData data, IReadOnlyList<int> fields,
        bool overwrite, IReadOnlyDictionary<int, int>? manual = null,
        IReadOnlySet<int>? excluded = null, IReadOnlySet<int>? allowZero = null)
    {
        manual ??= new Dictionary<int, int>();
        excluded ??= ImmutableHashSet<int>.Empty;
        allowZero ??= ImmutableHashSet<int>.Empty;
        var source = data.Source;
        var target = data.Target;
        var byId = target.Rows.ToDictionary(r => r.Id);
        var bySystem = new Dictionary<(string[], string?, string), List<RateRow>>();
        var byName = new Dictionary<string, List<RateRow>>();
        var byLineage = new Dictionary<int, List<RateRow>>();
        foreach (var row in target.Rows)
        {
            Add(bySystem, SystemKey(row), row);
            Add(byName, row.Key, row);
            Add(byLineage, row.Lineage, row);
        }
        var sourceSystems = new Dictionary<(string[], string?, string), List<RateRow>>();
        foreach (var row in source.Rows)
            Add(sourceSystems, SystemKey(row), row);
        var sameRoot = source.Root == target.Root;
        var sourceLineages = sameRoot
            ? source.Rows.GroupBy(r => r.Lineage).ToDictionary(g => g.Key, g => g.Count())
            : new Dictionary<int, int>();

        var matches = new List<(RateRow Source, RateRow? Target, bool Exact, string Status)>();
        foreach (var row in source.Rows)
        {
            if (!fields.Any(f => HasRate(row, f))) continue;
            if (excluded.Contains(row.Id))
            {
                matches.Add((row, null, false, "Исключено"));
                continue;
            }
            if (manual.TryGetValue(row.Id, out var chosenId))
            {
                var chosen = byId.GetValueOrDefault(chosenId);
                if (chosen is not null && Compatible(row, chosen))
                {
                    matches.Add((row, chosen, false, ""));
                    continue;
                }
            }
            var lineage = sameRoot ? byLineage.GetValueOrDefault(row.Lineage) : new List<RateRow>();
            if (lineage.Count > 0)
            {
                if (lineage.Count == sourceLineages.GetValueOrDefault(row.Lineage) &&
                    sourceLineages.GetValueOrDefault(row.Lineage) == 1 && Compatible(row, lineage[0]))
                    matches.Add((row, lineage[0], true, ""));
                else
                    matches.Add((row, null, false, "Конфликт этапов"));
                continue;
            }
            if (sameRoot && source.Id != source.Root && row.Lineage != row.Id)
            {
                matches.Add((row, null, false, "Выбрать вручную"));
                continue;
            }
            var key = SystemKey(row);
            var candidates = ExactCandidates(row, bySystem.GetValueOrDefault(key) ?? new List<RateRow>());
            var reverse = candidates.Count == 1
                ? ExactCandidates(candidates[0], sourceSystems.GetValueOrDefault(key) ?? new List<RateRow>())
                : new List<RateRow>();
            if (reverse.Count == 1 && reverse[0].Id == row.Id && row.Name.Trim().Length > 0)
                matches.Add((row, candidates[0], true, ""));
            else
            {
                var status = candidates.Count > 0 || byName.ContainsKey(row.Key)
                    ? "Выбрать вручную" : "Не найдено";
                matches.Add((row, null, false, status));
            }
        }

        var claimed = matches.Where(m => m.Target is not null)
            .GroupBy(m => m.Target!.Id).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<TransferRow>();
        foreach (var (row, chosen, exact, status) in matches)
        {
            if (chosen is null)
            {
                result.Add(new TransferRow(row, null, ImmutableArray<(int, decimal)>.Empty, status, false));
                continue;
            }
            if (claimed.GetValueOrDefault(chosen.Id) > 1)
            {
                result.Add(new TransferRow(row, chosen, ImmutableArray<(int, decimal)>.Empty,
                    "Несколько источников", false));
                continue;
            }
            var changes = new List<(int Field, decimal Amount)>();
            var blocked = "";
            var filled = false;
            var zeroReview = false;
            foreach (var field in fields)
            {
                if (!HasRate(row, field)) continue;
                var currentValue = chosen.Rate(field);
                var proposed = row.Rate(field)!;
                if (currentValue == proposed) { filled = true; continue; }
                if ((field == RateFields.PurchasePrice || field == RateFields.Price)
                    && EstimateStatistics.IsWorkName(chosen.Name))
                {
                    blocked = "Цена строки монтажа";
                    continue;
                }
                if (field == RateFields.Price)
                {
                    if (data.ProtectedPrices.Contains(chosen.Id))
                    {
                        blocked = "Цена связана с актом или этапом";
                        continue;
                    }
                    if (source.Vat != target.Vat)
                    {
                        blocked = "Разные условия НДС";
                        continue;
                    }
                    if (currentValue == 0m && !overwrite && !allowZero.Contains(row.Id))
                    {
                        zeroReview = true;
                        continue;
                    }
                }
                if (currentValue is not null && !overwrite &&
                    !(field == RateFields.Price && currentValue == 0m && allowZero.Contains(row.Id)))
                {
                    filled = true;
                    continue;
                }
                changes.Add((field, proposed!.Value));
            }
            string final;
            if (changes.Count > 0)
            {
                final = changes.Any(c => chosen.Rate(c.Field) is not null) ? "Заменить" : "Заполнить";
                if (blocked.Length > 0) final += $" · {blocked}";
                else if (zeroReview) final += " · проверить 0 ₽";
            }
            else
            {
                final = blocked.Length > 0 ? blocked
                    : zeroReview ? "Проверить 0 ₽"
                    : filled ? "Уже есть расценки" : "Нет расценок";
            }
            result.Add(new TransferRow(row, chosen, changes.ToImmutableArray(), final, exact, zeroReview));
        }
        return result;
    }

    private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<TValue>();
        list.Add(value);
    }

    internal static string Money(decimal value) =>
        value.ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
}
