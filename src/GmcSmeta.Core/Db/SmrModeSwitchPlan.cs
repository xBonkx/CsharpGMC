using System;
using System.Collections.Generic;

namespace GmcSmeta.Core.Db;

/// <summary>
/// Read-only financial contract for #57, consumed by stages/editor owners (#58/#59).
/// No caller may interpret a refused plan as consent to rewrite persisted prices.
/// </summary>
public sealed record SmrModeSwitchSnapshot(
    string CurrentMode,
    string Revision,
    bool Locked,
    bool IsStageChild,
    bool StageMoney,
    bool HasActsOrPostedQuantities,
    bool HasLinkedSupplementaryAgreements,
    decimal StoredSmrAmount,
    decimal PositionSmrAmount,
    int PricesThatWouldBeOverwritten,
    int WorkPriceFormulasAtRisk);

public sealed record SmrModeSwitchDifference(string Field, string Before, string After);

public sealed record SmrModeSwitchPlan(
    string CurrentMode,
    string TargetMode,
    string ExpectedRevision,
    bool Allowed,
    string? RefusalReason,
    decimal? BeforeSmr,
    decimal? AfterSmr,
    IReadOnlyList<SmrModeSwitchDifference> Differences)
{
    public bool IsNoOp => CurrentMode == TargetMode;
}

/// <summary>
/// Conservative SAFE/REFUSE classifier. Python's sum/pos conversions may
/// rewrite work_price, formulas or smr_amount. Until reversible snapshots and
/// full cross-document golden tests exist, only a metadata-only conversion
/// with identical persisted monetary representations can be SAFE.
/// </summary>
public static class SmrModeSwitchPlanning
{
    public static SmrModeSwitchPlan Build(SmrModeSwitchSnapshot state, string targetMode)
    {
        ArgumentNullException.ThrowIfNull(state);
        var from = state.CurrentMode;
        var supported = from is "sum" or "pos" or "percent" or "systems";
        var validTarget = targetMode is "sum" or "pos" or "percent" or "systems";
        var before = from switch
        {
            "sum" => state.StoredSmrAmount,
            "pos" => state.PositionSmrAmount,
            _ => (decimal?)null, // percent/systems need their own source-specific calculation.
        };
        var after = targetMode switch
        {
            "sum" => state.StoredSmrAmount,
            "pos" => state.PositionSmrAmount,
            _ => (decimal?)null,
        };

        SmrModeSwitchPlan Refuse(string reason, IReadOnlyList<SmrModeSwitchDifference> diff) =>
            new(from, targetMode, state.Revision, false, reason, before, after, diff);

        if (!supported || !validTarget)
            return Refuse("Неизвестный режим СМР. Запись запрещена.", Array.Empty<SmrModeSwitchDifference>());

        // A real no-op never copies prices, resets formulas, bumps revision or edits signed documents.
        if (from == targetMode)
            return new(from, targetMode, state.Revision, true, null, before, before,
                Array.Empty<SmrModeSwitchDifference>());

        var changes = new List<SmrModeSwitchDifference>
        {
            new("contracts.smr_mode", from, targetMode),
        };
        // smr_amount перечисляем как изменение только когда оно реально меняет значение;
        // при равных суммах (единственный разрешённый случай) это metadata-only переход.
        if (from == "pos" && targetMode == "sum" && state.StoredSmrAmount != state.PositionSmrAmount)
            changes.Add(new("contracts.smr_amount", state.StoredSmrAmount.ToString("G29"),
                state.PositionSmrAmount.ToString("G29")));
        if (targetMode == "pos" && state.PricesThatWouldBeOverwritten > 0)
            changes.Add(new("positions.work_price", "сохранённые значения",
                $"перезапись {state.PricesThatWouldBeOverwritten} позиций из work_sell"));

        if (state.Locked || state.IsStageChild || state.StageMoney)
            return Refuse("Подписанный документ или денежные этапы: смена запрещена.", changes);
        if (state.HasActsOrPostedQuantities)
            return Refuse("Связанные КС или проведённые объёмы: смена режима запрещена.", changes);
        if (state.HasLinkedSupplementaryAgreements)
            return Refuse("Связанные ДС: нельзя менять финансовую модель без согласования связей.", changes);
        if (from is "percent" or "systems" || targetMode is "percent" or "systems")
            return Refuse("Переход из/в percent/systems не доказан без потерь: остаётся BLOCKED.", changes);
        if (state.PricesThatWouldBeOverwritten != 0 || state.WorkPriceFormulasAtRisk != 0)
            return Refuse("Потребуется переписать ручные цены или формулы работ.", changes);
        if (state.StoredSmrAmount != state.PositionSmrAmount)
            return Refuse("Суммы sum и pos различаются или при записи изменится smr_amount.", changes);

        // The sole writable case: identical monetary representations, only mode metadata changes.
        return new(from, targetMode, state.Revision, true, null, before, after, changes);
    }
}
