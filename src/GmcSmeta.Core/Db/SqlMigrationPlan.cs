using System.Text.RegularExpressions;

namespace GmcSmeta.Core.Db;

/// <summary>Explicit Python-compatible routes; no production write or automatic upgrade.</summary>
public static class SqlMigrationPlan
{
    public static string? Script(int current, int target)
    {
        if (current is < 28 or > 32 || target is < 28 or > 32 || current > target)
            throw new InvalidOperationException($"Неподдерживаемый переход схемы {current} → {target}.");
        if (current == target) return null;
        if (target == 32) throw new InvalidOperationException("Переход договоров выполняется согласованным мигратором после резервной копии.");
        return target switch
        {
            29 => "migrate_discount_v29.sql",
            30 => "migrate_ks_sections_v30.sql",
            31 => "migrate_ks_discount_v31.sql",
            _ => throw new InvalidOperationException("Не зарегистрирован маршрут миграции.")
        };
    }

    public static void RequireRehearsalSource(string name)
    {
        if (!Regex.IsMatch(name ?? "", @"\AGmcSmetaPilot_(?:Test|ReleaseCheck)_[A-Za-z0-9_]{1,80}\z",
            RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Миграционная проверка разрешена только для явно названной отдельной базы Test_ или ReleaseCheck_.");
    }
}
