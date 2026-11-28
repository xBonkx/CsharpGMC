using System.Text.Json;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure validation for copying a retail/purchase price to all identical
/// top-level positions, mirroring Python builder.same_positions.
/// SQL writer must re-read the contract inside its own transaction.
/// </summary>
public static class PriceLikeBatchPlan
{
    public const int MaxChanges = 10000;

    public static bool CanStart(bool? locked, bool conflict, bool unsettled, bool detached, string revision) =>
        locked == false && !conflict && !unsettled && !detached && !string.IsNullOrWhiteSpace(revision);

    public sealed record Position(long Id, string Name, string Unit, bool IsRoot,
        bool Outside, decimal Price, decimal? PurchasePrice, string? Formulas);
    public sealed record Plan(decimal? Price, IReadOnlyList<long> TargetIds);

    public sealed record MenuPreview(decimal ClientPrice, int ClientCount,
        decimal? PurchasePrice, int PurchaseCount)
    {
        public IReadOnlyList<long> ClientIds { get; init; } = Array.Empty<long>();
        public IReadOnlyList<long> PurchaseIds { get; init; } = Array.Empty<long>();
        public Plan ExpectedPlan(string field) => field switch
        {
            "price" => new(ClientPrice, ClientIds),
            "purchase_price" => new(PurchasePrice, PurchaseIds),
            _ => throw new ArgumentException("Некорректное поле цены.", nameof(field))
        };
    }

    public static bool PositivePrice(decimal? price) => price is > 0m && price <= NumberText.MaxNumber;

    public static bool Matches(Plan expected, Plan actual) =>
        expected.Price == actual.Price &&
        expected.TargetIds.Count == actual.TargetIds.Count &&
        expected.TargetIds.OrderBy(id => id).SequenceEqual(actual.TargetIds.OrderBy(id => id));

    // Use the full displayed revision, not the filtered grid. The SQL writer
    // still independently validates revision, targets and formulas in its transaction.
    public static MenuPreview Preview(IReadOnlyList<EstimateRow> positions, long sourceId)
    {
        var source = positions.FirstOrDefault(p => p.Id == sourceId && !p.IsSystem);
        if (source is null || source.Id <= 0 || source.IsSub || source.OutOfKp ||
            source.ParentPositionId is not null || string.IsNullOrWhiteSpace(source.Name))
            return new(0m, 0, null, 0);

        var client = PositivePrice(source.Price);
        var purchase = PositivePrice(source.PurchasePrice);
        var key = Catalog.Key(source.Name, source.Unit);
        var clientIds = new List<long>();
        var purchaseIds = new List<long>();
        foreach (var row in positions)
        {
            if (row.Id <= 0 || row.Id == source.Id || row.IsSystem || row.IsSub ||
                row.ParentPositionId is not null || row.OutOfKp ||
                Catalog.Key(row.Name, row.Unit) != key)
                continue;
            if (client && row.Price != source.Price) clientIds.Add(row.Id);
            if (purchase && row.PurchasePrice != source.PurchasePrice) purchaseIds.Add(row.Id);
        }
        return new(source.Price, clientIds.Count, source.PurchasePrice, purchaseIds.Count)
        {
            ClientIds = clientIds.ToArray(),
            PurchaseIds = purchaseIds.ToArray()
        };
    }

    public static Plan Build(IReadOnlyList<Position> rows, long sourceId, string field)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (sourceId <= 0 || field is not ("price" or "purchase_price"))
            throw new InvalidOperationException("Некорректный источник или поле цены.");

        var source = rows.FirstOrDefault(p => p.Id == sourceId);
        if (source is null || !source.IsRoot || source.Outside ||
            string.IsNullOrWhiteSpace(source.Name))
            throw new InvalidOperationException(
                "Исходная строка удалена либо не является верхней позицией.");

        var price = field == "price" ? source.Price : source.PurchasePrice;
        if (price < 0m || price > NumberText.MaxNumber)
            throw new InvalidOperationException("Исходная цена вне допустимого диапазона.");

        var key = Catalog.Key(source.Name, source.Unit);
        var targets = rows.Where(p => p.Id != source.Id &&
                p.IsRoot && !p.Outside &&
                Catalog.Key(p.Name, p.Unit) == key &&
                (field == "price" ? p.Price != price : p.PurchasePrice != price))
            .ToArray();
        if (targets.Length > MaxChanges)
            throw new InvalidOperationException(
                "Слишком много строк для одного безопасного переноса цены.");

        // JSON_MODIFY only accepts valid JSON. Validate all candidate rows
        // before running an UPDATE; don't silently reset broken formulas.
        foreach (var row in targets)
        {
            if (row.Id <= 0)
                throw new InvalidOperationException("В группе некорректный ID позиции.");
            if (string.IsNullOrWhiteSpace(row.Formulas)) continue;
            try
            {
                using var json = JsonDocument.Parse(row.Formulas);
                if (json.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException(
                        $"Формулы позиции {row.Id} не являются JSON-объектом.");
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Повреждены формулы позиции {row.Id}; перенос отменён.", ex);
            }
        }
        if (targets.Select(p => p.Id).Distinct().Count() != targets.Length)
            throw new InvalidOperationException("Повторяющиеся ID одинаковых позиций.");
        return new Plan(price, targets.Select(p => p.Id).ToArray());
    }
}
