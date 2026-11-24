using Microsoft.Data.Sqlite;

namespace GmcSmeta.Core.Db;

/// <summary>База открыта более новой версией программы: старым в неё писать нельзя.</summary>
public sealed class OldProgramException : Exception
{
    public string BaseVersion { get; }
    public string OurVersion { get; }

    public OldProgramException(string baseVersion, string ourVersion)
        : base($"С этой базой уже работает более новая версия программы ({baseVersion}).\n" +
               $"У вас версия {ourVersion} — она устарела.\n\n" +
               "Чтобы не испортить общие данные, старая версия базу не открывает.\n" +
               "Обновите программу на этом компьютере и запустите снова.")
    {
        BaseVersion = baseVersion;
        OurVersion = ourVersion;
    }
}

/// <summary>
/// Открытие базы 1:1 как в Python-версии: журнал DELETE (никакого WAL на сетевой шаре),
/// busy_timeout 15 с, 64 МБ кэша, короткие транзакции. Схема и версия — общие с Python.
/// </summary>
public static class Database
{
    public const int BusyTimeoutMs = 15_000;
    public const long CacheBytes = -65_536;   // отрицательное = килобайты

    private static readonly string[] IndexSql =
    {
        "CREATE INDEX IF NOT EXISTS ix_change_log_contract ON change_log (contract_id)",
        "CREATE INDEX IF NOT EXISTS ix_change_log_object ON change_log (object_id)",
        "CREATE INDEX IF NOT EXISTS ix_positions_system_id ON positions (system_id)",
        "CREATE INDEX IF NOT EXISTS ix_positions_parent_id ON positions (parent_id)",
        "CREATE INDEX IF NOT EXISTS ix_positions_stage_src ON positions (stage_src)",
    };

    /// <summary>Открыть файл базы. Каталог создаётся; прагмы — как в Python-версии.</summary>
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        if (!readOnly)
        {
            Exec(conn, "PRAGMA journal_mode=DELETE");   // на сетевой шаре WAL нельзя
            Exec(conn, "PRAGMA temp_store=MEMORY");
        }
        Exec(conn, $"PRAGMA busy_timeout={BusyTimeoutMs}");
        Exec(conn, "PRAGMA foreign_keys=ON");
        Exec(conn, $"PRAGMA cache_size={CacheBytes}");
        return conn;
    }

    /// <summary>
    /// Открыть рабочую базу: под замком, проверить версию, при необходимости создать схему,
    /// дополнить колонки и заполнить начальные данные (фирмы, теги), отметить версию программы.
    /// Возвращает уже готовое соединение.
    /// </summary>
    public static SqliteConnection OpenBase(string path, string appVersion = AppConfig.AppVersion)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var initLock = InitLock.Acquire(full);
        var conn = Open(full);
        try
        {
            if (TableExists(conn, "meta"))
                CheckVersion(conn, appVersion);      // до любой миграции

            EnsureSchema(conn);                      // create_all: недостающие таблицы
            Migrations.AddMissingColumns(conn);      // новые колонки в старых базах
            Seeding.Run(conn);                       // фирмы, теги, логотипы, версия схемы

            StampVersion(conn, appVersion);
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    /// <summary>Создать недостающие таблицы и индексы (существующие не трогает).</summary>
    public static void EnsureSchema(SqliteConnection conn)
    {
        foreach (var statement in Schema.CreateSql.Split(';'))
        {
            var sql = statement.Trim();
            if (sql.Length == 0) continue;
            if (sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
                sql = "CREATE TABLE IF NOT EXISTS " + sql["CREATE TABLE ".Length..];
            Exec(conn, sql);
        }
        foreach (var sql in IndexSql) Exec(conn, sql);
    }

    /// <summary>Отказать старой программе; ничего не менять.</summary>
    public static void CheckVersion(SqliteConnection conn, string ourVersion)
    {
        var baseVersion = GetMeta(conn, "app_version") ?? "0";
        if (CompareVersions(baseVersion, ourVersion) > 0)
            throw new OldProgramException(baseVersion, ourVersion);

        if (int.TryParse(GetMeta(conn, "schema_version"), out var schema) && schema > Schema.Version)
            throw new OldProgramException(baseVersion, ourVersion);
    }

    public static void StampVersion(SqliteConnection conn, string appVersion) =>
        SetMeta(conn, "app_version", appVersion);

    public static string? GetMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public static void SetMeta(SqliteConnection conn, string key, string value)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta (key, value) VALUES ($k, $v) " +
                          "ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public static bool TableExists(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Сравнение версий программ: &lt;0 — a старее b, 0 — равны, &gt;0 — a новее.
    /// Как version_tuple в Python: нечисловая часть считается нулём.
    /// </summary>
    public static int CompareVersions(string a, string b) => VersionTuple(a).CompareTo(VersionTuple(b));

    /// <summary>«0.66.4» → (0, 66, 4); нечисловые части считаются нулём.</summary>
    public static (int, int, int) VersionTuple(string version)
    {
        var parts = (version ?? "0").Split('.');
        int At(int i)
        {
            if (i >= parts.Length) return 0;
            return int.TryParse(parts[i], out var v) ? v : 0;
        }
        return (At(0), At(1), At(2));
    }

    public static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>
/// Замок подготовки базы: две копии программы (или двойной клик) не должны создавать
/// таблицы одновременно. Файл создаётся атомарно, как os.O_EXCL в Python-версии.
/// </summary>
public sealed class InitLock : IDisposable
{
    private const int WaitMs = 60_000;
    private const int StaleMs = 120_000;
    private const int StepMs = 300;

    private readonly string _path;
    private FileStream? _stream;

    public InitLock(string dbPath)
    {
        _path = dbPath + ".init-lock";
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Enter()
    {
        var started = Environment.TickCount64;
        while (true)
        {
            try
            {
                _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                return;
            }
            catch (IOException)
            {
                try
                {
                    if (File.Exists(_path) &&
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(_path) > TimeSpan.FromMilliseconds(StaleMs))
                    {
                        File.Delete(_path);      // замок остался от упавшей программы
                        continue;
                    }
                }
                catch (IOException) { continue; }   // владелец уже удалил: пробуем снова

                if (Environment.TickCount64 - started > WaitMs)
                    throw new TimeoutException(
                        "База сейчас подготавливается другой копией программы и не освободилась за минуту.\n" +
                        $"Если программа больше нигде не запущена — удалите файл\n{_path}");

                Thread.Sleep(StepMs);
            }
        }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        try { File.Delete(_path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static IDisposable Acquire(string dbPath)
    {
        var l = new InitLock(dbPath);
        l.Enter();
        return l;
    }
}
