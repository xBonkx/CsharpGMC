namespace GmcSmeta.Core.Db;

/// <summary>Legal identity is distinct from document type and editable display metadata.</summary>
public static class AgreementEditPolicy
{
    // Null is a real state, not permission to guess a legal contract from the object.
    public static long? InheritParent(long? preferred, long? parentLegal)
    {
        if (preferred.HasValue && preferred != parentLegal)
            throw new ArgumentException("Договор ДС или этапа должен совпадать с договором основания.");
        return parentLegal;
    }

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
