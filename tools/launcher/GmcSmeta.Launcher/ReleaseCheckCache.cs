using System.Net.Http.Headers;
using System.Text.Json;

namespace GmcSmeta.Launcher;

// Exact local installation and update policy; never contains a credential.
internal sealed record ReleaseCheckKey(string Repository, string Version, string Flavor,
    string Exe, string InstalledUtc, string LauncherVersion, string PreferredFlavor, bool NoSelfUpdate);

internal sealed record ReleaseCheckCache(ReleaseCheckKey Key, string? ETag, DateTimeOffset CheckedUtc)
{
    internal static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public bool IsFresh(DateTimeOffset now) =>
        now >= CheckedUtc && now - CheckedUtc < Ttl;

    public static EntityTagHeaderValue? ParseETag(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 1024 &&
        EntityTagHeaderValue.TryParse(value, out var parsed) && parsed.Tag != "*"
            ? parsed : null;

    public static ReleaseCheckCache? Load(string path, ReleaseCheckKey key)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return null;
            var state = JsonSerializer.Deserialize<ReleaseCheckCache>(File.ReadAllText(path));
            if (state is null || state.Key != key || state.CheckedUtc > DateTimeOffset.UtcNow ||
                state.CheckedUtc == default) return null;
            return state with { ETag = ParseETag(state.ETag)?.ToString() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // Best-effort cache only: losing it must never prevent an update or startup.
    // Called under the existing launcher mutex, after the install and self-update.
    public static void Save(string path, ReleaseCheckKey key, string? etag, DateTimeOffset now)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var state = new ReleaseCheckCache(key, ParseETag(etag)?.ToString(), now);
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
