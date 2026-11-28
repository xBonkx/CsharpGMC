using System.Text.Json;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Conservative preflight for restoring *open* KP / non-archived objects only.
/// No mutation is allowed when a snapshot carries signed/issued/linked history or
/// a foreign key that could be silently nulled by the legacy restore algorithm.
/// This is not a replacement for server-side role checks in the same transaction.
/// </summary>
public static class TrashRestoreSafety
{
    private static readonly string[] ProtectedSections =
    [
        "acts", "act_lines", "shipments", "shipment_lines", "movements",
        "procurement", "smr_costs", "spec_revisions", "attachments",
        "access_lists", "contract_links", "price_links", "metal_links", "agreement_files"
    ];

    public static void ValidateSnapshot(JsonElement root, string kind, bool allowStage = false)
    {
        if (root.ValueKind != JsonValueKind.Object || kind is not ("object" or "contract"))
            throw new InvalidDataException("Недопустимый снимок корзины.");

        foreach (var section in ProtectedSections)
            if (Rows(root, section).Count != 0)
                throw new InvalidOperationException(
                    "Восстановление с актами, выдачами, закупками, вложениями или внешними связями " +
                    "требует отдельной подтверждённой процедуры.");

        // Forward schema changes must never be dropped silently by the restore loop.
        // Only explicitly supported, independent table groups may contain rows.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "objects", "contracts", "systems", "positions", "agreements"
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
        {
            if (!seen.Add(field.Name))
                throw new InvalidDataException("Повтор раздела корзины.");
            if (allowed.Contains(field.Name) || ProtectedSections.Contains(field.Name, StringComparer.Ordinal))
                continue;
            // Reject even empty unknown groups: a schema change requires a reviewed
            // restore implementation, not silent best-effort omission.
            throw new InvalidDataException("Неизвестный раздел корзины: " + field.Name);
        }

        var objects = Rows(root, "objects");
        var contracts = Rows(root, "contracts");
        var systems = Rows(root, "systems");
        var positions = Rows(root, "positions");
        if (kind == "object" && objects.Count != 1 ||
            kind == "contract" && (objects.Count != 0 || contracts.Count != 1))
            throw new InvalidDataException("Количество корневых записей не совпадает.");

        var linkedStage = allowStage && kind == "contract" && HasReference(contracts[0], "stage_of");
        var objectIds = Ids(objects);
        var legalCards = Rows(root, "agreements");
        _ = Ids(legalCards);
        foreach (var legal in legalCards)
        {
            if (kind != "object") throw new InvalidDataException("Договор должен восстанавливаться вместе со своим объектом.");
            MustReference(legal, "object_id", objectIds);
            if (!IsFalse(legal, "archived"))
                throw new InvalidOperationException("Архивный или неполный юридический договор не восстанавливается автоматически.");
        }
        var contractIds = Ids(contracts);
        var systemIds = Ids(systems);
        var positionIds = Ids(positions);

        foreach (var obj in objects)
            if (!IsFalse(obj, "archived"))
                throw new InvalidOperationException("Архивный объект не восстанавливается автоматически.");

        foreach (var contract in contracts)
        {
            if (!IsFalse(contract, "locked") ||
                !TryText(contract, "kind", out var type) || type != "kp" ||
                (!linkedStage && HasReference(contract, "stage_of")) || IsTrue(contract, "stage_rest"))
                throw new InvalidOperationException("Только незакрытые КП допускаются к восстановлению.");
            if (kind == "object")
            {
                MustReference(contract, "object_id", objectIds);
            }
            // KP and its stages are not legal annexes. A populated legal link or
            // annex number is inconsistent even if the linked card is in this dump.
            if (HasReference(contract, "agreement_id") || HasAnnexNumber(contract))
                throw new InvalidOperationException("КП содержит юридическую связь приложения. Требуется отдельная проверка.");
            // The current restore drops missing external company/parent links to NULL.
            // Do not accept that loss under the restricted restoration policy.
            if ((!linkedStage && HasReference(contract, "company_id")) || HasReference(contract, "base_contract_id"))
                throw new InvalidOperationException("Внешние ссылки КП требуют отдельной проверки.");
        }

        foreach (var system in systems)
        {
            MustReference(system, "contract_id", contractIds);
            OptionalReference(system, "parent_id", systemIds);
            if (!linkedStage && HasReference(system, "stage_sys_src"))
                throw new InvalidOperationException("Ссылки этапов не проверены.");
        }

        foreach (var position in positions)
        {
            MustReference(position, "system_id", systemIds);
            OptionalReference(position, "parent_id", positionIds);
            if (!linkedStage && (HasReference(position, "stage_src") ||
                HasReference(position, "catalog_item_id") ||
                HasReference(position, "supplier_id")))
                throw new InvalidOperationException("Внешние ссылки позиции требуют проверки.");
        }
    }

    // A snapshot can outlive the schema that created it. Never let the
    // legacy column filter silently discard an independently stored agreement.
    public static void ValidateLegalSchema(JsonElement root, bool legalEnabled)
    {
        if (!legalEnabled && (Rows(root, "agreements").Count > 0 ||
            Rows(root, "agreement_files").Count > 0 ||
            Rows(root, "contracts").Any(c => HasReference(c, "agreement_id") || HasAnnexNumber(c))))
            throw new InvalidOperationException("Снимок содержит юридические реквизиты. Требуется совместимая схема договоров.");
    }

    public static void ValidateForget(JsonElement root, string kind)
    {
        ValidateSnapshot(root, kind);
        if (Rows(root, "agreements").Count > 0)
            throw new InvalidOperationException("Снимок содержит самостоятельные юридические договоры. Окончательное удаление требует отдельного согласования.");
    }

    // Legal cards have a reviewed complete SQL row snapshot, unlike legacy
    // optional columns. Missing/unknown/duplicate fields must not default away.
    public static void ValidateLegalColumns(JsonElement root, IEnumerable<string> columns)
    {
        var expected = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        foreach (var card in Rows(root, "agreements"))
        {
            var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in card.EnumerateObject())
                if (!actual.Add(property.Name))
                    throw new InvalidDataException("Повтор поля юридического договора в корзине.");
            if (!actual.SetEquals(expected))
                throw new InvalidOperationException("Состав полей юридического договора изменился. Восстановление без потери реквизитов невозможно.");
        }
    }

    private static bool HasAnnexNumber(JsonElement row) =>
        row.TryGetProperty("annex_number", out var value) &&
        value.ValueKind != JsonValueKind.Null &&
        (value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(value.GetString()));

    private static List<JsonElement> Rows(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var token)) return [];
        if (token.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Повреждён раздел корзины: " + name);
        var result = new List<JsonElement>();
        foreach (var row in token.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Повреждена строка корзины: " + name);
            result.Add(row);
        }
        return result;
    }

    private static HashSet<long> Ids(IReadOnlyList<JsonElement> rows)
    {
        var result = new HashSet<long>();
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("id", out var value) ||
                !value.TryGetInt64(out var id) || id <= 0 || !result.Add(id))
                throw new InvalidDataException("Отсутствует ID или найден повтор ID в корзине.");
        }
        return result;
    }

    private static bool TryText(JsonElement row, string field, out string text)
    {
        text = "";
        if (!row.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString() ?? "";
        return true;
    }

    private static bool IsFalse(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.False;

    private static bool IsTrue(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) &&
        value.ValueKind == JsonValueKind.True;

    private static bool HasReference(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) &&
        value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static void MustReference(JsonElement row, string field, HashSet<long> allowed)
    {
        if (!row.TryGetProperty(field, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var id) || !allowed.Contains(id))
            throw new InvalidOperationException("Несовпадающая связь корзины: " + field);
    }

    private static void OptionalReference(JsonElement row, string field, HashSet<long> allowed)
    {
        if (!HasReference(row, field)) return;
        MustReference(row, field, allowed);
    }
}
