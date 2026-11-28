namespace GmcSmeta.Core.Db;

/// <summary>Legal identity is distinct from document type and editable display metadata.</summary>
public static class AgreementEditPolicy
{
    public static long? ResolveRequested(string kind, string oldKind, long? previous,
        long? preferred, bool baseChanged, long? inherited)
    {
        if (kind == "kp") return null;
        if (kind == "dop")
        {
            if (preferred.HasValue && preferred != inherited)
                throw new ArgumentException("Юридический договор ДС должен совпадать с основанием.");
            if (!baseChanged && oldKind == "dop")
                return previous;
            if (!inherited.HasValue)
                throw new InvalidOperationException("У выбранного основания нет юридического договора. Сначала проверьте его реквизиты.");
            return inherited;
        }
        return preferred ?? previous;
    }
}
