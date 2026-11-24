using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace GmcSmeta.Core.Db;

/// <summary>Only client connection preferences live on disk; no document data is cached locally.</summary>
public sealed record RemoteSqlProfile
{
    public string Name { get; init; } = "Домашний SQL Server";
    public string Server { get; init; } = "";
    public int Port { get; init; } = 1433;
    public string Database { get; init; } = "GmcSmetaPilot_Laptop";
    public bool WindowsAuthentication { get; init; }
    public string UserName { get; init; } = "";
    public bool TrustServerCertificate { get; init; }
    public string ProtectedPassword { get; init; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Укажите название профиля.");
        if (string.IsNullOrWhiteSpace(Server) ||
            Server.IndexOfAny([';', ',', '=', '\r', '\n']) >= 0 ||
            Server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Укажите IP или имя сервера без порта и префикса tcp:.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("Порт должен быть от 1 до 65535.");
        if (!Database.StartsWith("GmcSmetaPilot_", StringComparison.OrdinalIgnoreCase) ||
            Database.Length <= "GmcSmetaPilot_".Length)
            throw new ArgumentException("Ноутбук подключается только к GmcSmetaPilot_*.");
        if (!WindowsAuthentication && string.IsNullOrWhiteSpace(UserName))
            throw new ArgumentException("Введите логин SQL Server.");
    }

    public string ConnectionString(string password = "")
    {
        Validate();
        var cs = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:{Server.Trim()},{Port}",
            InitialCatalog = Database.Trim(),
            IntegratedSecurity = WindowsAuthentication,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = TrustServerCertificate,
            PersistSecurityInfo = false,
            ApplicationName = "GMC-Smeta C# Pilot",
            ConnectTimeout = 5
        };
        if (!WindowsAuthentication)
        {
            cs.UserID = UserName.Trim();
            cs.Password = password;
        }
        return cs.ConnectionString;
    }
}

/// <summary>Profiles are intentionally separate from Python's GMC-Smeta settings.</summary>
public static class RemoteSqlProfileStore
{
    public static string FilePath
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "GMC-Smeta-Next", "sql-profiles.json");
        }
    }

    public static List<RemoteSqlProfile> Load()
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<RemoteSqlProfile>>(File.ReadAllText(FilePath)) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Do not erase the unreadable file. The user can repair it manually.
            throw new InvalidOperationException("Не удалось прочитать профили SQL Server. Проверьте sql-profiles.json.", e);
        }
    }

    public static void Save(IReadOnlyList<RemoteSqlProfile> profiles)
    {
        foreach (var profile in profiles) profile.Validate();
        var target = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(profiles,
                new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string ProtectPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Защита пароля доступна только в Windows.");
        var raw = Encoding.UTF8.GetBytes(password);
        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }

    public static string UnprotectPassword(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return "";
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Пароль DPAPI доступен только в Windows.");
        var raw = ProtectedData.Unprotect(Convert.FromBase64String(encrypted),
            null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(raw); }
        finally { CryptographicOperations.ZeroMemory(raw); }
    }
}

public sealed record RemoteSqlCheck(bool NetworkOk, bool LoginOk, bool SchemaOk,
    string Message, long NetworkMilliseconds, long SqlMilliseconds)
{
    public bool Success => NetworkOk && LoginOk && SchemaOk;
}

/// <summary>Read-only diagnostics: socket, authentication, then schema version 28.</summary>
public static class RemoteSqlProbe
{
    public static async Task<RemoteSqlCheck> CheckAsync(RemoteSqlProfile profile,
        string password, CancellationToken cancellationToken = default)
    {
        profile.Validate(); // Includes production DB protection before opening any socket.
        var clock = Stopwatch.StartNew();
        try
        {
            using var tcp = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await tcp.ConnectAsync(profile.Server.Trim(), profile.Port, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, false, "Нет ответа TCP от сервера. Проверьте IP, порт, TCP/IP и брандмауэр.",
                clock.ElapsedMilliseconds, 0);
        }
        catch (SocketException)
        {
            return new(false, false, false, "Сервер/порт недоступен. Проверьте сеть и TCP/IP SQL Server.",
                clock.ElapsedMilliseconds, 0);
        }
        var networkMs = clock.ElapsedMilliseconds;
        clock.Restart();
        var loggedIn = false;
        try
        {
            await using var connection = new SqlConnection(profile.ConnectionString(password));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            loggedIn = true;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT [value] FROM dbo.meta WHERE [key]=N'schema_version'";
            cmd.CommandTimeout = 5;
            var version = Convert.ToString(
                await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            var required = SqlSchemaCompatibility.WritableSchemaVersion.ToString(CultureInfo.InvariantCulture);
            if (version != required)
                return new(true, true, false, $"Схема несовместима: требуется {required}, получено {version ?? "нет версии"}.",
                    networkMs, clock.ElapsedMilliseconds);
            return new(true, true, true, $"Сервер доступен, авторизация выполнена, схема версии {required} подходит.",
                networkMs, clock.ElapsedMilliseconds);
        }
        catch (SqlException ex)
        {
            var message = ex.Number switch
            {
                18456 => "Ошибка входа SQL Server. Проверьте логин, пароль и режим Mixed Mode.",
                4060 => "База не существует или у пользователя нет доступа к ней.",
                916 or 229 or 230 => "Недостаточно прав для чтения тестовой базы.",
                208 => "В базе нет dbo.meta: восстановите правильную тестовую схему.",
                _ => "SQL Server отклонил подключение. Проверьте TLS-сертификат, доступ к базе и состояние службы."
            };
            return new(true, loggedIn, false, message, networkMs, clock.ElapsedMilliseconds);
        }
        catch (InvalidOperationException)
        {
            return new(true, false, false, "Параметры подключения не подходят для SQL Server.",
                networkMs, clock.ElapsedMilliseconds);
        }
    }
}
