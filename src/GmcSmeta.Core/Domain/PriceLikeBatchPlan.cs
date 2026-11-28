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
        decimal? PurchasePrice, int PurchaseCount);

    // Use the full displayed revision, not the filtered grid. The SQL writer
    // still independently validates revision, targets and formulas in its transaction.
    public static MenuPreview Preview(IReadOnlyList<EstimateRow> positions, long sourceId)
    {
        var source = positions.FirstOrDefault(p => p.Id == sourceId && !p.IsSystem);
        if (source is null || source.IsSub || source.ParentPositionId is not null ||
            string.IsNullOrWhiteSpace(source.Name))
            return new(0m, 0, null, 0);

        var client = source.Price > 0m && source.Price <= NumberText.MaxNumber;
        var purchase = source.PurchasePrice is decimal value && value > 0m &&
            value <= NumberText.MaxNumber;
        var key = Catalog.Key(source.Name, source.Unit);
        int clientCount = 0, purchaseCount = 0;
        foreach (var row in positions)
        {
            if (row.Id == source.Id || row.IsSystem || row.IsSub ||
                row.ParentPositionId is not null || row.OutOfKp ||
                Catalog.Key(row.Name, row.Unit) != key)
                continue;
            if (client && row.Price != source.Price) clientCount++;
            if (purchase && row.PurchasePrice != source.PurchasePrice) purchaseCount++;
        }
        return new(source.Price, clientCount, source.PurchasePrice, purchaseCount);
    }

    public static Plan Build(IReadOnlyList<Position> rows, long sourceId, string field)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (sourceId <= 0 || field is not ("price" or "purchase_price"))
            throw new InvalidOperationException("Некорректный источник или поле цены.");

        var source = rows.FirstOrDefault(p => p.Id == sourceId);
        if (source is null || !source.IsRoot ||
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
