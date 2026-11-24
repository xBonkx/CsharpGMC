namespace GmcSmeta.Core.Domain;

/// <summary>
/// Pure, SQL-independent safety rules for edits to a stage document.
/// The DB caller must load this state under its serializable write transaction.
/// A stage_sum is a signed contractual target, not an editable estimate amount.
/// </summary>
public static class StageMutationPolicy
{
    public enum Operation { Quantity, Rename, Target, Delete }

    public sealed record State(
        long? StageOf, bool IsRest, bool StageLocked,
        decimal? SignedTarget, bool ParentLocked,
        string ParentKind, long? ParentStageOf);

    public static string? Denial(long fullId, State stage, Operation operation)
    {
        if (fullId <= 0 || stage.StageOf != fullId ||
            stage.ParentStageOf is not null ||
            !string.Equals(stage.ParentKind, "kp", StringComparison.OrdinalIgnoreCase))
            return "Этап не принадлежит выбранному полному КП.";
        if (stage.ParentLocked)
            return "Полное КП заблокировано: распределение этапов нельзя изменять.";
        if (stage.IsRest)
            return "Документ «Остаток» рассчитывается автоматически и не редактируется.";
        if (stage.StageLocked)
            return "Подписанный или заблокированный этап нельзя изменять.";
        if (stage.SignedTarget is not null)
            return "Для этапа указана сумма подписанного договора. Изменения возможны только через согласованное переоформление.";
        return null;
    }

    public static bool ChangesQuantity(decimal oldQuantity, decimal newQuantity) =>
        Math.Abs(oldQuantity - newQuantity) > 0.000001m;
}
