using System.Globalization;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure preview of Python area.collapse(): classify real root positions by
/// material and calculate unit quantities without modifying any source data.
/// Database dependencies (acts, movements, shipments) are checked separately
/// by the SQL writer and are never trusted from the UI.
/// </summary>
public static class AreaCollapsePlan
{
    public const int MaxPositions = 10000;

    public sealed record Position(long Id, long SystemId, string SystemName,
        string Name, string Unit, decimal Qty, decimal Price, decimal? Purchase,
        double? Manual, int Order, string ExtraJson, bool Outside,
        bool HasChildren, bool HasDependencies);

    public sealed record Candidate(long Id, long SystemId, int Order,
        decimal Area, decimal Amount, decimal PurchaseAmount);

    public sealed record Group(long? OriginalSystemId, string Key, string Title,
        IReadOnlyList<Candidate> Rows)
    {
        public decimal Area => Rows.Sum(x => x.Area);
        public decimal Money => Rows.Sum(x => x.Amount);
        public decimal Purchase => Rows.Sum(x => x.PurchaseAmount);
    }

    public sealed record Result(IReadOnlyList<Group> Groups, int Excluded,
        int NoArea)
    {
        public int Rows => Groups.Sum(x => x.Rows.Count);
    }

    public static Result Build(IReadOnlyCollection<Position> all, bool whole)
    {
        ArgumentNullException.ThrowIfNull(all);
        if (all.Count > MaxPositions)
            throw new ArgumentException("Слишком много позиций для безопасного сворачивания.");
        var groups = new Dictionary<(long? System, string Key), (string Title, List<Candidate> Rows)>();
        var excluded = 0;
        var noArea = 0;
        foreach (var row in all.OrderBy(x => x.SystemId).ThenBy(x => x.Order).ThenBy(x => x.Id))
        {
            if (row.Id <= 0 || row.SystemId <= 0)
                throw new InvalidOperationException("Некорректные ID позиций КП.");
            if (row.Outside || Area.IsSummary(row.ExtraJson) ||
                row.SystemName == Area.AllSystems) continue;
            var material = Area.Material(row.Name, row.Unit);
            if (material is null)
            {
                if (Area.IsDuctFamily(row.Name) ||
                    row.Name.Contains("огнезащ", StringComparison.OrdinalIgnoreCase) ||
                    row.Name.Contains("изоляц", StringComparison.OrdinalIgnoreCase) ||
                    row.Name.Contains("теплоизол", StringComparison.OrdinalIgnoreCase))
                    noArea++;
                continue;
            }
            var computed = Area.RowArea(row.Name, row.Unit, (double)row.Qty, row.Manual);
            if (computed is not double value || !double.IsFinite(value) || value <= 0d)
            {
                noArea++;
                continue;
            }
            if (row.HasChildren || row.HasDependencies)
            {
                excluded++;
                continue;
            }
            if (row.Qty < 0m || row.Price < 0m ||
                row.Purchase < 0m || value > (double)NumberText.MaxNumber)
            {
                excluded++;
                continue;
            }
            var key = (whole ? (long?)null : row.SystemId, material.Value.Key);
            if (!groups.TryGetValue(key, out var group))
            {
                group = (material.Value.Title, new List<Candidate>());
                groups.Add(key, group);
            }
            var area = Convert.ToDecimal(value);
            group.Rows.Add(new Candidate(row.Id, row.SystemId, row.Order,
                area, row.Qty * row.Price, row.Qty * (row.Purchase ?? 0m)));
        }
        var sorted = groups.OrderBy(x => x.Key.System ?? 0)
            .ThenBy(x => Priority(x.Key.Key))
            .ThenBy(x => x.Value.Title, StringComparer.Ordinal)
            .Select(x => new Group(x.Key.System, x.Key.Key,
                x.Value.Title, x.Value.Rows)).ToArray();
        return new Result(sorted, excluded, noArea);
    }

    private static int Priority(string key) =>
        key.StartsWith("steel:", StringComparison.Ordinal) ? 0 :
        key.StartsWith("fire:", StringComparison.Ordinal) ? 1 :
        key.StartsWith("insul:", StringComparison.Ordinal) ? 2 : 9;

    public static decimal SummaryQty(IEnumerable<Candidate> rows) =>
        decimal.Round(rows.Sum(x => x.Area), 3, MidpointRounding.ToEven);

    public sealed record SummaryValues(decimal Qty, decimal Price, decimal? PurchasePrice);

    public sealed record SummaryComponent(string Name, string Unit, decimal Qty,
        decimal Price, decimal? PurchasePrice, double? ManualArea);

    /// <summary>
    /// Calculate an m² summary from the original child rows, never from the
    /// summary's previously rounded price. This avoids accumulating rounding
    /// drift when the same material is collapsed multiple times or a child
    /// has been edited. It does not change any original row or its metadata.
    /// </summary>
    public static SummaryValues SumSourceRows(IReadOnlyCollection<SummaryComponent> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        if (children.Count is 0 or > MaxPositions)
            throw new InvalidOperationException("Сводная строка м² пуста или содержит слишком много позиций.");

        decimal area = 0m;
        decimal money = 0m;
        decimal purchase = 0m;
        foreach (var child in children)
        {
            if (child.Qty < 0m || child.Price < 0m || child.PurchasePrice < 0m)
                throw new InvalidOperationException("Отрицательные суммы в исходных строках м².");
            var computed = Area.RowArea(child.Name, child.Unit,
                (double)child.Qty, child.ManualArea);
            // An invalid child may be the result of an external edit. Do not
            // silently drop its original money or overwrite the summary.
            if (computed is not double value || !double.IsFinite(value) || value <= 0d)
                throw new InvalidOperationException(
                    "Площадь вложенной позиции не определяется. Разворачивать данные безопаснее.");
            area += Convert.ToDecimal(value);
            money += child.Qty * child.Price;
            purchase += child.Qty * (child.PurchasePrice ?? 0m);
        }

        var qty = decimal.Round(area, 3, MidpointRounding.ToEven);
        if (qty <= 0m || qty > NumberText.MaxNumber)
            throw new InvalidOperationException("Общая площадь м² вне допустимого диапазона.");
        return new SummaryValues(qty, PricePerSquareMeter(money, area),
            purchase == 0m ? null : PricePerSquareMeter(purchase, area));
    }

    /// <summary>
    /// An existing m² summary may receive additional source rows in a later
    /// collapse. Its price must be reweighted with the old and new amounts,
    /// not left unchanged while its quantity grows.
    /// Existing child rows retain their original quantities and prices.
    /// </summary>
    public static SummaryValues ExtendSummary(decimal existingQty, decimal existingPrice,
        decimal? existingPurchase, decimal extraArea, decimal extraMoney,
        decimal extraPurchase)
    {
        if (existingQty < 0m || existingPrice < 0m || existingPurchase < 0m ||
            extraArea <= 0m || extraMoney < 0m || extraPurchase < 0m)
            throw new InvalidOperationException("Некорректные значения для объединения строк м².");

        var qty = decimal.Round(existingQty + extraArea, 3, MidpointRounding.ToEven);
        if (qty <= 0m || qty > NumberText.MaxNumber)
            throw new InvalidOperationException("Общая площадь м² вне допустимого диапазона.");

        // For a newly created summary preserve Python's initial per-area
        // pricing; for an existing one combine monetary totals with weights.
        var price = existingQty == 0m
            ? PricePerSquareMeter(extraMoney, extraArea)
            : PricePerSquareMeter(existingQty * existingPrice + extraMoney, qty);

        decimal? purchase = null;
        if (existingPurchase is not null || extraPurchase != 0m)
            purchase = existingQty == 0m
                ? PricePerSquareMeter(extraPurchase, extraArea)
                : PricePerSquareMeter(existingQty * (existingPurchase ?? 0m) + extraPurchase, qty);

        return new SummaryValues(qty, price, purchase);
    }

    public static decimal PricePerSquareMeter(decimal amount, decimal area) =>
        area <= 0m || amount == 0m ? 0m :
        decimal.Round(amount / area, 2, MidpointRounding.ToEven);

    public static string ExistingKey(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return "";
        try
        {
            using var obj = System.Text.Json.JsonDocument.Parse(metadata);
            if (obj.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                obj.RootElement.TryGetProperty("m2", out var key) &&
                key.ValueKind == System.Text.Json.JsonValueKind.String)
                return key.GetString() ?? "";
        }
        catch (System.Text.Json.JsonException) { }
        return "";
    }

    /// <summary>
    /// Parse origin metadata from an existing collapsed row. Invalid and
    /// orphaned metadata is not silently ignored during expansion.
    /// </summary>
    public static (long System, int Order)? OriginalLocation(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return null;
        try
        {
            using var obj = System.Text.Json.JsonDocument.Parse(metadata);
            if (obj.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !obj.RootElement.TryGetProperty("m2_sys", out var system) ||
                !obj.RootElement.TryGetProperty("m2_order", out var order) ||
                system.ValueKind != System.Text.Json.JsonValueKind.Number ||
                order.ValueKind != System.Text.Json.JsonValueKind.Number ||
                !system.TryGetInt64(out var systemId) ||
                !order.TryGetInt32(out var position) ||
                systemId <= 0 || position < 0)
                return null;
            return (systemId, position);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string CollapsedMetadata(string? original, long systemId, int order)
    {
        if (systemId <= 0 || order < 0)
            throw new InvalidOperationException("Неверная исходная позиция для сворачивания м².");
        var obj = ParseObject(original);
        if (obj.ContainsKey("m2") || obj.ContainsKey("m2_sys") ||
            obj.ContainsKey("m2_order"))
            throw new InvalidOperationException(
                "У позиции уже есть служебные поля м². Повторное сворачивание отменено.");
        obj["m2_sys"] = systemId;
        obj["m2_order"] = order;
        return obj.ToJsonString();
    }

    public static string RestoredMetadata(string? original)
    {
        var obj = ParseObject(original);
        obj.Remove("m2_sys");
        obj.Remove("m2_order");
        return obj.Count == 0 ? "" : obj.ToJsonString();
    }

    private static System.Text.Json.Nodes.JsonObject ParseObject(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata)) return new();
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(metadata)
                as System.Text.Json.Nodes.JsonObject ??
                throw new InvalidOperationException("Некорректные метаданные позиции.");
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new InvalidOperationException("Некорректный JSON позиции — изменение отменено.", e);
        }
    }
}
