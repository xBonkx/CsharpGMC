using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core.Domain;

namespace GmcSmeta.Core.Db;

public enum ExcelExistingChangeKind { Added, Changed, Unchanged, DeletedInSource, Ambiguous, Conflict }
public enum ExcelExistingAction { Skip, ApplyQuantity, AddPosition }

/// <summary>Read-only SQL snapshot. Values are rechecked under a write lock before committing.</summary>
public sealed record ExcelExistingDbRow(
    long Id, long SystemId, long? ParentId, string SectionPath, string ParentName,
    string Number, string Name, string Unit, decimal Quantity, decimal Price,
    decimal? PurchasePrice, decimal? WorkPrice, string Note, string Formulas,
    int? SourceRow, string ExtraJson, bool HasDependencies, bool HasSignedAct,
    bool IsImported);

public sealed record ExcelExistingSourceRow(
    int Ordinal, int? ParentOrdinal, string SectionPath, string ParentName,
    string Number, string Name, string Unit, decimal Quantity, decimal Price,
    decimal? PurchasePrice, decimal? WorkPrice, string Note, int SourceRow,
    long? StableId, string Basis = "", string ExtraJson = "");

public sealed class ExcelExistingDifference
{
    public required ExcelExistingChangeKind Kind { get; init; }
    public required ExcelExistingSourceRow? Source { get; init; }
    public required ExcelExistingDbRow? Existing { get; init; }
    public required string Explanation { get; init; }
    public bool SafeToApply { get; init; }
    /// <summary>Only proven, unmodified source quantities may be selected in bulk.</summary>
    public bool SafeForBulk { get; init; }
    public ExcelExistingAction Action { get; set; } = ExcelExistingAction.Skip;
    public bool Apply
    {
        get => Action != ExcelExistingAction.Skip;
        set => Action = value && SafeToApply
            ? Kind == ExcelExistingChangeKind.Added
                ? ExcelExistingAction.AddPosition : ExcelExistingAction.ApplyQuantity
            : ExcelExistingAction.Skip;
    }
    public string DisplayName => Source?.Name ?? Existing?.Name ?? "";
    public string DisplayIdentity => "Excel row " + (Source?.SourceRow.ToString(CultureInfo.InvariantCulture) ?? "—") +
        " / stable " + (Source?.StableId?.ToString(CultureInfo.InvariantCulture) ?? "—") +
        " → SQL " + (Existing?.Id.ToString(CultureInfo.InvariantCulture) ?? "—");
    public string DisplaySystem => Source?.SectionPath ?? Existing?.SectionPath ?? "";
    public string DisplayQuantity => (Existing?.Quantity.ToString(CultureInfo.InvariantCulture) ?? "—") +
        " → " + (Source?.Quantity.ToString(CultureInfo.InvariantCulture) ?? "—");
    public string DisplayPrice => (Existing?.Price.ToString(CultureInfo.InvariantCulture) ?? "—") +
        " / Excel: " + (Source?.Price.ToString(CultureInfo.InvariantCulture) ?? "—");
}

/// <summary>
/// Deterministic and fail-closed matcher: no fuzzy matches, duplicate collapsing, or deletion.
/// Existing prices, formulas and notes cannot be chosen for replacement.
/// </summary>
public sealed class ExcelExistingUpdatePlan
{
    public long ContractId { get; }
    public string Revision { get; }
    public string FileSha256 { get; }
    public string ScopeKey { get; }
    public string PlanId { get; }
    public IReadOnlyList<ExcelExistingDifference> Differences { get; }

    // Only for rebuilding independently reviewed mappings; SQL always validates live state.
    private IReadOnlyList<ExcelExistingDbRow> _originalRows = Array.Empty<ExcelExistingDbRow>();
    private IReadOnlyList<ExcelExistingSourceRow> _sourceRows = Array.Empty<ExcelExistingSourceRow>();
    private IReadOnlyCollection<string> _availableSystems = Array.Empty<string>();
    private IReadOnlyDictionary<int, long> _manualMappings = new Dictionary<int, long>();
    private bool _contractLocked;
    private string _originalScope = "";

    private ExcelExistingUpdatePlan(long contractId, string revision, string fileSha256,
        string scopeKey, string planId, List<ExcelExistingDifference> differences)
    {
        ContractId = contractId;
        Revision = revision;
        FileSha256 = fileSha256;
        ScopeKey = scopeKey;
        PlanId = planId;
        Differences = differences;
    }

    public static string Normalize(string? value, bool unit = false)
    {
        var s = (value ?? "").Replace('\u00a0', ' ').Replace('ё', 'е').Replace('Ё', 'Е');
        s = string.Join(" ", s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
        return unit ? s.TrimEnd('.').Replace("²", "2").Replace("³", "3") : s;
    }

    private static bool HasUnchangedImportQuantity(ExcelExistingDbRow row)
    {
        if (string.IsNullOrWhiteSpace(row.ExtraJson)) return false;
        try
        {
            using var json = JsonDocument.Parse(row.ExtraJson);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("excel_imported_qty", out var item) ||
                item.ValueKind != JsonValueKind.String ||
                !decimal.TryParse(item.GetString(), NumberStyles.Number,
                    CultureInfo.InvariantCulture, out var imported))
                return false;
            return imported == row.Quantity;
        }
        catch (JsonException) { return false; }
    }

    private static string MatchName(ExcelExistingDbRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.ExtraJson))
        {
            try
            {
                using var json = JsonDocument.Parse(row.ExtraJson);
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("src", out var src) &&
                    src.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(src.GetString()))
                    return src.GetString()!;
            }
            catch (JsonException) { /* Keep the visible name; never guess provenance. */ }
        }
        return row.Name;
    }

    private static string Key(string system, string parent, string name, string unit) =>
        Normalize(system) + "\u001f" + Normalize(parent) + "\u001f" +
        Normalize(name) + "\u001f" + Normalize(unit, true);

    /// <summary>
    /// Candidates have the same contract-scoped section, parent, source name and unit.
    /// Changed identities, invented IDs and m:n mappings remain blocked.
    /// </summary>
    public IReadOnlyList<ExcelExistingDbRow> MappingCandidates(int sourceOrdinal, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var source = _sourceRows.SingleOrDefault(r => r.Ordinal == sourceOrdinal);
        if (source is null || source.ParentOrdinal is not null) return Array.Empty<ExcelExistingDbRow>();
        var key = Key(source.SectionPath, source.ParentName, source.Name, source.Unit);
        var reserved = _manualMappings.Where(m => m.Key != sourceOrdinal)
            .Select(m => m.Value).ToHashSet();
        var result = _originalRows.Where(old =>
            {
                token.ThrowIfCancellationRequested();
                return Key(old.SectionPath, old.ParentName, MatchName(old), old.Unit) == key &&
                    (source.StableId is null || source.StableId == old.Id) &&
                    !reserved.Contains(old.Id);
            }).OrderBy(old => old.Id).ToArray();
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>
    /// Explicit one-to-one assignment. Replanning discards checkbox selections.
    /// </summary>
    public ExcelExistingUpdatePlan WithExplicitMapping(int sourceOrdinal, long existingId,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Differences.Any(d => d.Source?.Ordinal == sourceOrdinal &&
                d.Kind == ExcelExistingChangeKind.Ambiguous))
            throw new InvalidOperationException("Ручное сопоставление разрешено только для неоднозначных строк.");
        if (!MappingCandidates(sourceOrdinal, token).Any(row => row.Id == existingId))
            throw new InvalidOperationException("ID не является безопасным кандидатом этого договора.");
        var mappings = new Dictionary<int, long>(_manualMappings) { [sourceOrdinal] = existingId };
        return Build(ContractId, Revision, FileSha256, _originalRows,
            _sourceRows, _availableSystems, _contractLocked, _originalScope, mappings, token);
    }

    public static IReadOnlyList<ExcelExistingSourceRow> Flatten(IReadOnlyList<ExcelImportBlock> blocks,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var result = new List<ExcelExistingSourceRow>();
        var ordinal = 0;
        foreach (var block in blocks)
        foreach (var section in block.Sections)
        {
            var path = string.IsNullOrWhiteSpace(section.Parent)
                ? section.Name : section.Parent + " / " + section.Name;
            foreach (var position in section.Positions)
            {
                token.ThrowIfCancellationRequested();
                var top = ++ordinal;
                result.Add(NewRow(position, top, null, path, ""));
                foreach (var child in position.Children)
                {
                    token.ThrowIfCancellationRequested();
                    result.Add(NewRow(child, ++ordinal, top, path, position.Name));
                }
            }
        }
        return result;
    }

    private static ExcelExistingSourceRow NewRow(ExcelImportPosition p, int ordinal,
        int? parent, string system, string parentName)
    {
        long? stable = null;
        foreach (var key in new[] { "gmc_position_id", "position_id" })
            if (p.Extra.TryGetValue(key, out var id) &&
                long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                stable = parsed;
                break;
            }
        var basis = p.Extra.TryGetValue("basis", out var b) ? b : "";
        var extra = p.Extra.Count == 0 ? "" : JsonSerializer.Serialize(p.Extra);
        return new(ordinal, parent, system, parentName, p.Number, p.Name, p.Unit,
            p.Quantity, p.Price, p.PurchasePrice, p.WorkPrice, p.Note, p.SourceRow,
            stable, basis, extra);
    }

    public static ExcelExistingUpdatePlan Build(long contractId, string revision, string fileSha256,
        IReadOnlyList<ExcelExistingDbRow> existing,
        IReadOnlyList<ExcelExistingSourceRow> incoming,
        IReadOnlyCollection<string> availableSystems, bool contractLocked = false,
        string scopeKey = "", IReadOnlyDictionary<int, long>? explicitMappings = null,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (contractId <= 0 || string.IsNullOrWhiteSpace(revision) ||
            fileSha256.Length != 64 || !fileSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Нет корректного договора, ревизии или SHA-256 Excel.");
        if (incoming.Count > 100_000 || incoming.Count == 0)
            throw new ArgumentException("Выбери непустой Excel до 100 000 позиций.");
        var sourceByOrdinal = new Dictionary<int, ExcelExistingSourceRow>(incoming.Count);
        foreach (var source in incoming)
        {
            token.ThrowIfCancellationRequested();
            if (!sourceByOrdinal.TryAdd(source.Ordinal, source))
                throw new ArgumentException("Повторяющиеся идентификаторы исходных строк.");
        }
        var mappings = explicitMappings is null
            ? new Dictionary<int, long>() : new Dictionary<int, long>(explicitMappings);
        if (mappings.Values.Distinct().Count() != mappings.Count)
            throw new InvalidOperationException("Несколько Excel-строк нельзя сопоставить одной SQL-позиции.");
        var byId = existing.ToDictionary(r => r.Id);
        foreach (var mapping in mappings)
        {
            token.ThrowIfCancellationRequested();
            var source = sourceByOrdinal.GetValueOrDefault(mapping.Key);
            if (source is null || source.ParentOrdinal is not null ||
                !byId.TryGetValue(mapping.Value, out var current) ||
                source.StableId is long stable && stable != current.Id ||
                Key(source.SectionPath, source.ParentName, source.Name, source.Unit) !=
                Key(current.SectionPath, current.ParentName, MatchName(current), current.Unit))
                throw new InvalidOperationException("Ручное сопоставление нарушает stable ID/раздел/родителя/имя/единицу.");
        }
        var reservedIds = mappings.Values.ToHashSet();
        var originalNames = existing.ToDictionary(r => r.Id, MatchName);
        var byKey = existing.GroupBy(r => Key(r.SectionPath, r.ParentName, originalNames[r.Id], r.Unit))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var nearNames = new HashSet<string>(existing.Select(r =>
            Normalize(r.SectionPath) + "\u001f" + Normalize(r.ParentName) + "\u001f" +
            Normalize(originalNames[r.Id])), StringComparer.Ordinal);
        var nearNumbers = new HashSet<string>(existing.Where(r => !string.IsNullOrWhiteSpace(r.Number))
            .Select(r => Normalize(r.SectionPath) + "\u001f" + Normalize(r.ParentName) +
                "\u001f" + Normalize(r.Number) + "\u001f" + Normalize(r.Unit, true)), StringComparer.Ordinal);
        var incomingGroups = incoming.GroupBy(r => Key(r.SectionPath, r.ParentName, r.Name, r.Unit))
            .ToDictionary(g => g.Key, g => g.Count());
        var available = new HashSet<string>(availableSystems.Select(x => Normalize(x)), StringComparer.Ordinal);
        token.ThrowIfCancellationRequested();
        var matched = new HashSet<long>();
        var diffs = new List<ExcelExistingDifference>(incoming.Count + existing.Count);

        foreach (var source in incoming.OrderBy(x => x.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(source.Name) || source.Name.Length > 2000 ||
                source.Unit.Length > 32 || source.Number.Length > 32 ||
                source.Note.Length > 10_000 || source.Basis.Length > 64 ||
                source.Quantity < 0 || source.Price < 0 ||
                source.PurchasePrice < 0 || source.WorkPrice < 0)
                throw new ArgumentException("Неверные значения Excel, строка " + source.SourceRow);
            var key = Key(source.SectionPath, source.ParentName, source.Name, source.Unit);
            var candidates = byKey.GetValueOrDefault(key) ?? Array.Empty<ExcelExistingDbRow>();
            ExcelExistingDbRow? row = null;
            string? issue = null;
            if (mappings.TryGetValue(source.Ordinal, out var explicitlyChosenId))
            {
                row = byId[explicitlyChosenId]; // validated 1:1 contract-scoped mapping
            }
            else if (source.StableId is long stableId)
            {
                if (!byId.TryGetValue(stableId, out row))
                    issue = "Указанный устойчивый ID отсутствует в выбранном договоре.";
                else if (Key(row.SectionPath, row.ParentName, originalNames[row.Id], row.Unit) != key)
                    issue = "Устойчивый ID указывает на строку с другой системой, родителем, именем или единицей.";
            }
            else if (incomingGroups[key] > 1 || candidates.Length > 1)
                issue = "Повторяющиеся строки Excel или несколько одинаковых позиций в смете. Автовыбор запрещён.";
            else if (candidates.Length == 1)
                row = candidates[0];
            if (!mappings.ContainsKey(source.Ordinal) && row is not null &&
                reservedIds.Contains(row.Id))
                issue = "SQL-позиция закреплена за другой Excel-строкой вручную.";

            if (issue is not null || (row is not null && !matched.Add(row.Id)))
            {
                diffs.Add(new ExcelExistingDifference
                {
                    Kind = ExcelExistingChangeKind.Ambiguous, Source = source, Existing = row,
                    Explanation = issue ?? "Несколько строк Excel претендуют на одну позицию."
                });
                continue;
            }

            if (row is null)
            {
                // Similar old rows with a manually edited name/unit are conflicts,
                // not automatic additions. Never silently create a duplicate.
                var nearName = Normalize(source.SectionPath) + "\u001f" +
                    Normalize(source.ParentName) + "\u001f" + Normalize(source.Name);
                var nearNumber = Normalize(source.SectionPath) + "\u001f" +
                    Normalize(source.ParentName) + "\u001f" + Normalize(source.Number) +
                    "\u001f" + Normalize(source.Unit, true);
                var near = nearNames.Contains(nearName) ||
                    source.Number.Length > 0 && nearNumbers.Contains(nearNumber);
                var canAdd = !near && !contractLocked && source.ParentOrdinal is null &&
                    available.Contains(Normalize(source.SectionPath)) && incomingGroups[key] == 1;
                diffs.Add(new ExcelExistingDifference
                {
                    Kind = canAdd ? ExcelExistingChangeKind.Added : ExcelExistingChangeKind.Conflict,
                    Source = source, Existing = null, SafeToApply = canAdd,
                    SafeForBulk = canAdd,
                    Explanation = canAdd ? "Новая позиция в существующем разделе. Цены только для новой позиции." :
                    "Похожая существующая строка, новый/неоднозначный раздел, новый компонент либо заблокированный договор. Добавление требует проверки."
                });
                continue;
            }

            var quantityChanged = row.Quantity != source.Quantity;
            var priceChanged = row.Price != source.Price ||
                row.PurchasePrice != source.PurchasePrice || row.WorkPrice != source.WorkPrice;
            var noteChanged = !string.Equals(row.Note, source.Note, StringComparison.Ordinal);
            var restricted = contractLocked || !row.IsImported || row.HasDependencies ||
                row.HasSignedAct || !string.IsNullOrWhiteSpace(row.Formulas) &&
                row.Formulas != "{}";
            var hasBaseline = HasUnchangedImportQuantity(row);
            var warning = restricted
                ? "Изменение объёма защищено: ручная строка, формула, связь с КС/ДС/этапами/закупками либо блокировка."
                : hasBaseline
                    ? "Исходный объём соответствует зафиксированному значению Excel."
                    : "Исходный объём не подтверждён: возможно ручное изменение. Только индивидуальное подтверждение; массовый выбор запрещён.";
            if (priceChanged) warning += " Цены Excel отличаются; сохранены ручные цены сметы.";
            if (noteChanged) warning += " Примечание отличается; исходное примечание сохранено.";
            diffs.Add(new ExcelExistingDifference
            {
                Kind = quantityChanged ? (restricted ? ExcelExistingChangeKind.Conflict : ExcelExistingChangeKind.Changed)
                    : priceChanged || noteChanged ? ExcelExistingChangeKind.Conflict : ExcelExistingChangeKind.Unchanged,
                Source = source, Existing = row,
                SafeToApply = quantityChanged && !restricted,
                SafeForBulk = quantityChanged && !restricted && hasBaseline,
                Explanation = warning
            });
        }

        foreach (var old in existing)
        {
            token.ThrowIfCancellationRequested();
            if (!matched.Contains(old.Id))
                diffs.Add(new ExcelExistingDifference
                {
                    Kind = ExcelExistingChangeKind.DeletedInSource, Source = null,
                    Existing = old, Explanation = "Нет в Excel. Позиция будет сохранена; автоматическое удаление запрещено."
                });
        }

        // PlanId binds one reviewed source to one revision and the exact read snapshot.
        var serial = new StringBuilder();
        var scopeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey))).ToLowerInvariant();
        serial.Append(contractId).Append('|').Append(revision).Append('|')
            .Append(fileSha256.ToLowerInvariant()).Append('|').Append(scopeHash);
        foreach (var old in existing.OrderBy(x => x.Id))
        {
            token.ThrowIfCancellationRequested();
            serial.Append('|').Append(JsonSerializer.Serialize(old));
        }
        foreach (var row in incoming.OrderBy(x => x.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            serial.Append('|').Append(JsonSerializer.Serialize(row));
        }
        foreach (var mapping in mappings.OrderBy(m => m.Key))
        {
            token.ThrowIfCancellationRequested();
            serial.Append("|manual:").Append(mapping.Key).Append("->").Append(mapping.Value);
        }
        token.ThrowIfCancellationRequested();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial.ToString())));
        var plan = new ExcelExistingUpdatePlan(contractId, revision, fileSha256.ToLowerInvariant(),
            scopeHash, id, diffs)
        {
            _originalRows = existing.ToArray(),
            _sourceRows = incoming.ToArray(),
            _availableSystems = availableSystems.ToArray(),
            _contractLocked = contractLocked,
            _originalScope = scopeKey,
            _manualMappings = mappings
        };
        token.ThrowIfCancellationRequested();
        return plan;
    }

    public string OperationId()
    {
        var actions = string.Join("|", Differences
            .Where(d => d.Action != ExcelExistingAction.Skip)
            .OrderBy(d => d.Source?.Ordinal ?? int.MaxValue)
            .Select(d => (d.Source?.Ordinal.ToString(CultureInfo.InvariantCulture) ?? "") +
                ":" + d.Action + ":" + (d.Existing?.Id.ToString(CultureInfo.InvariantCulture) ?? "")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PlanId + "|" + actions)));
    }

    public IReadOnlyList<ExcelExistingDifference> Selected()
    {
        var selected = Differences.Where(d => d.Action != ExcelExistingAction.Skip).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("Нет выбранных безопасных изменений.");
        if (selected.Any(d => !d.SafeToApply ||
            d.Action != (d.Kind == ExcelExistingChangeKind.Added
                ? ExcelExistingAction.AddPosition : ExcelExistingAction.ApplyQuantity)))
            throw new InvalidOperationException("Запрещено применять неоднозначные или защищённые изменения.");
        return selected;
    }
}
