using GmcSmeta.Core.Db;
using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Domain;

/// <summary>
/// Прикладной хелпер ядра: применить набор полей позиции внутри уже открытой транзакции
/// (перенос builder.update_position без лишней транзакции — её открывает вызывающий).
/// </summary>
public static partial class PositionEditor
{
    /// <summary>Записать поля позиции и историю; False — значений не изменилось. Locked проверяется.</summary>
    public static bool ApplyChanges(SqliteConnection conn, long id, params PositionChange[] changes)
    {
        var snapshot = Load(conn, id);
        if (snapshot.Locked)
            throw new InvalidOperationException("Смета подписана. Изменение недоступно.");
        var changed = Apply(conn, snapshot, changes);
        if (snapshot.ParentId is long parentId)
            Area.RefreshSummary(conn, parentId);
        return changed;
    }
}
