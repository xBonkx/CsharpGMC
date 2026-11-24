namespace GmcSmeta.Core.Domain;

/// <summary>
/// Guard money-preserving SMR mode switches before any SQL modifications.
/// The current WPF page supports 'sum' and 'pos'; Python's 'percent' and
/// 'systems' modes must stay intact until their formulas are implemented.
/// </summary>
public static class SmrModeSwitchPolicy
{
    public static string? Rejection(string? currentMode, string? targetMode,
        bool locked, bool isStageChild, bool stageMoney)
    {
        if (targetMode is not ("sum" or "pos"))
            return "Целевой режим СМР пока не поддерживается в C#.";
        if (locked)
            return "Подписанная смета недоступна для смены режима СМР.";
        if (isStageChild)
            return "Режим СМР внутри КП этапа менять нельзя.";
        if (stageMoney)
            return "СМР этого КП идёт из этапов — сменить режим нельзя.";
        if (currentMode is not ("sum" or "pos"))
            return "Текущий режим СМР пока не перенесён. " +
                   "Переключение может потерять сумму — используй Python-версию.";
        return null;
    }
}
