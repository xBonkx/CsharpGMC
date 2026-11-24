using System.Text.Json;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Versioned, contract-scoped undo evidence stored in change_log atomically with the
/// Excel mutation. No original price, formula, note or provenance is ever rewritten.
/// </summary>
public sealed record ExcelExistingQuantityUndoRow(
    long Id, long SystemId, long? ParentId, string Name, string Unit,
    decimal BeforeQuantity, decimal AppliedQuantity, decimal Price,
    decimal? PurchasePrice, decimal? WorkPrice, string Note, string Formulas,
    int? SourceRow, string ExtraJson);

public sealed record ExcelExistingUndoSnapshot(
    int Version, long ContractId, string OperationId, string PlanId,
    string BeforeRevision, string AfterRevision,
    IReadOnlyList<ExcelExistingQuantityUndoRow> QuantityEdits,
    IReadOnlyList<long> AddedPositionIds)
{
    public bool CanUndoQuantities =>
        Version == 1 && AddedPositionIds is { Count: 0 } &&
        QuantityEdits is { Count: > 0 };

    public void Validate(string operationId, long contractId)
    {
        if (Version != 1 || ContractId != contractId ||
            OperationId is null || OperationId != operationId || operationId.Length != 64 ||
            !operationId.All(Uri.IsHexDigit) || PlanId is null || PlanId.Length != 64 ||
            !PlanId.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(BeforeRevision) ||
            string.IsNullOrWhiteSpace(AfterRevision) ||
            QuantityEdits is null || AddedPositionIds is null ||
            QuantityEdits.Select(x => x.Id).Distinct().Count() != QuantityEdits.Count ||
            AddedPositionIds.Distinct().Count() != AddedPositionIds.Count ||
            QuantityEdits.Any(x => x.Id <= 0 || x.SystemId <= 0 ||
                x.BeforeQuantity < 0 || x.AppliedQuantity < 0) ||
            AddedPositionIds.Any(id => id <= 0))
            throw new InvalidOperationException("Версионированный снимок Excel повреждён или не соответствует договору.");
    }

    public static ExcelExistingUndoSnapshot Parse(string json, string operationId, long contractId)
    {
        ExcelExistingUndoSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ExcelExistingUndoSnapshot>(json) ??
                throw new InvalidOperationException("Снимок Excel пуст.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Некорректный формат undo snapshot.", ex);
        }
        snapshot.Validate(operationId, contractId);
        return snapshot;
    }
}
