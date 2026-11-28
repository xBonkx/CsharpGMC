using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using GmcSmeta.Core;

namespace GmcSmeta.Launcher;

internal sealed record ReleaseAsset(string Name, string ApiUrl, string BrowserUrl, long Size);

internal sealed record ReleaseInfo(string Tag, string Version, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>Токен доступа к закрытому репозиторию: переменная среды, файл или git credential manager.</summary>
internal static class GitHubAuth
{
    public static string? Resolve(out string source)
    {
        source = "";
        StartupTrace.Mark("launcher_auth_noninteractive");
        foreach (var name in new[] { "GMC_SMETA_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                source = "переменная " + name;
                return value.Trim();
            }
        }

        try
        {
            var file = AppPaths.TokenFile;
            if (File.Exists(file))
            {
                var value = File.ReadAllText(file).Trim();
                if (value.Length > 0)
                {
                    source = "файл " + file;
                    return value;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        string? fromGit;
        using (StartupTrace.MeasureElapsed("launcher_auth_gcm"))
            fromGit = FromGitCredentialManager();
        if (fromGit is not null)
        {
            source = "git credential manager";
            return fromGit;
        }
        return null;
    }

    private static string? FromGitCredentialManager()
    {
        try
        {
            var start = new ProcessStartInfo("git", "credential fill")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            // Не показывать окно входа GitHub: если пароля нет в хранилище — просто пропустить.
            start.Environment["GCM_INTERACTIVE"] = "never";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var process = Process.Start(start);
            if (process is null) return null;
            process.StandardInput.Write("protocol=https\nhost=github.com\n\n");
            process.StandardInput.Close();
            // Drain both pipes concurrently: ReadToEnd before WaitForExit defeated the timeout.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                StartupTrace.Mark("launcher_auth_gcm_timeout");
                try { process.Kill(true); } catch { }
                return null;
            }
            if (!Task.WaitAll(new Task[] { outputTask, errorTask }, 1000))
            {
                StartupTrace.Mark("launcher_auth_gcm_pipe_timeout");
                return null;
            }
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || error.Length > 0) return null;
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("password=", StringComparison.Ordinal))
                {
                    var value = trimmed["password=".Length..].Trim();
                    return value.Length > 0 ? value : null;
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or AggregateException)
        {
            return null;
        }
    }
}

/// <summary>Чтение GitHub Releases через REST API и скачивание вложений.</summary>
internal sealed class GitHubReleases : IDisposable
{
    public const string DefaultRepository = "xBonkx/CsharpGMC";
    public const string ChecksumAsset = "SHA256SUMS.txt";

    private readonly string _repository;
    private readonly string? _token;
    private readonly ConsoleUi _ui;
    private readonly HttpClient _http;

    public GitHubReleases(string repository, string? token, ConsoleUi ui)
    {
        _repository = repository;
        _token = token;
        _ui = ui;
        _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GMC-Smeta-Launcher", "1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    public ReleaseInfo? TryGetLatest(string? etag, out string? responseEtag, out bool notModified, out string? error)
    {
        error = null;
        responseEtag = null;
        notModified = false;
        try
        {
            using var document = GetJson($"https://api.github.com/repos/{_repository}/releases/latest", TimeSpan.FromSeconds(30),
                etag, out responseEtag, out notModified);
            if (notModified) return null;
            if (document is null) throw new InvalidOperationException("GitHub не вернул сведения о выпуске.");
            var root = document.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagElement) ? tagElement.GetString() ?? "" : "";
            if (tag.Length == 0)
            {
                error = "в ответе GitHub нет tag_name";
                return null;
            }

            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean())
            {
                error = "последний релиз — черновик";
                return null;
            }

            var assets = new List<ReleaseAsset>();
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                    var api = item.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    var browser = item.TryGetProperty("browser_download_url", out var browserElement)
                        ? browserElement.GetString() : null;
                    var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed)
                        ? parsed : 0L;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(api))
                        assets.Add(new ReleaseAsset(name, api, browser ?? api, size));
                }
            }

            return new ReleaseInfo(tag, AppVersion.Normalize(tag), assets);
        }
        catch (HttpRequestException ex)
        {
            error = Describe(ex);
            return null;
        }
        catch (OperationCanceledException)
        {
            error = "истекло время ожидания ответа GitHub";
            return null;
        }
        catch (JsonException ex)
        {
            error = "неожиданный ответ GitHub: " + ex.Message;
            return null;
        }
    }

    public string? TryGetChecksums(ReleaseInfo release)
    {
        var asset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, ChecksumAsset, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var request = CreateRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
            using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(response.Content.ReadAsStream(timeout.Token), Encoding.UTF8);
            return reader.ReadToEndAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _ui.Warn("Не удалось получить контрольные суммы: " + ex.Message);
            return null;
        }
    }

    public static string? FindChecksum(string? sums, string assetName)
    {
        if (string.IsNullOrWhiteSpace(sums)) return null;
        foreach (var raw in sums.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            var hash = line[..space].Trim();
            var name = line[(space + 1)..].Trim().TrimStart('*');
            if (string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase) && hash.Length == 64 && hash.All(Uri.IsHexDigit))
                return hash;
        }
        return null;
    }

    /// <summary>Скачивает вложение и, если известна, сверяет SHA-256. Возвращает фактический хеш.</summary>
    public string Download(ReleaseAsset asset, string destination, string? expectedSha256)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(60));
        using var request = CreateRequest(HttpMethod.Get, asset.ApiUrl, "application/octet-stream");
        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        headerTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var headers = StartupTrace.MeasureElapsed("launcher_asset_http_headers");
        using var response = _http.Send(request, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token);
        headers?.Dispose();
        headerTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                "вложение не найдено или нет доступа. Для закрытого репозитория нужен токен GitHub.");
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        if (asset.Size > 0 && total != asset.Size)
            throw new InvalidOperationException("Размер вложения не совпадает с опубликованным.");
        using var source = response.Content.ReadAsStream(timeout.Token);
        var lastReport = 0L;
        var started = Stopwatch.StartNew();
        string actual;
        try
        {
            using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var transfer = StartupTrace.MeasureElapsed("launcher_asset_transfer_with_hash");
            actual = ReleaseDownloadTransfer.CopyVerified(source, target, total, expectedSha256,
                timeout.Token, TimeSpan.FromSeconds(60), done =>
                {
                    if (started.ElapsedMilliseconds - lastReport > 250 || done == total)
                    {
                        lastReport = started.ElapsedMilliseconds;
                        Report(done, total);
                    }
                });
            transfer?.Dispose();
            using (StartupTrace.MeasureElapsed("launcher_asset_flush"))
                target.Flush(flushToDisk: true);
        }
        catch (OperationCanceledException ex)
        {
            StartupTrace.Mark("launcher_asset_transfer_timeout");
            throw new IOException("Истекло время скачивания вложения. Повторите запуск.", ex);
        }
        Console.WriteLine();

        if (string.IsNullOrWhiteSpace(expectedSha256))
            _ui.Warn("Контрольная сумма для «" + asset.Name + "» не найдена — SHA-256 не сверена с опубликованным значением.");
        return actual;
    }

    private void Report(long done, long total)
    {
        var text = total > 0
            ? $"{done / 1048576.0:0.0} / {total / 1048576.0:0.0} МБ ({done * 100 / total}%)"
            : $"{done / 1048576.0:0.0} МБ";
        _ui.DownloadProgress(text, total > 0 ? Math.Min(100, done * 100.0 / total) : null);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string url, string accept)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new HttpRequestException("Недопустимый адрес GitHub API.");
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        if (!string.IsNullOrWhiteSpace(_token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private JsonDocument? GetJson(string url, TimeSpan timeout, string? etag,
        out string? responseEtag, out bool notModified)
    {
        responseEtag = null;
        notModified = false;
        using var cts = new CancellationTokenSource(timeout);
        using var request = CreateRequest(HttpMethod.Get, url, "application/vnd.github+json");
        var conditional = ReleaseCheckCache.ParseETag(etag);
        if (conditional is not null) request.Headers.IfNoneMatch.Add(conditional);
        using var response = _http.Send(request, HttpCompletionOption.ResponseContentRead, cts.Token);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            if (conditional is null) throw new HttpRequestException("GitHub вернул 304 без условного запроса.");
            notModified = true;
            responseEtag = response.Headers.ETag?.ToString() ?? conditional.ToString();
            return null;
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("GitHub отклонил токен (401). Проверьте срок действия токена.");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new HttpRequestException("GitHub ограничил доступ (403): исчерпан лимит запросов или у токена нет прав на репозиторий.");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException(
                "репозиторий или релиз не найден (404). Репозиторий закрытый — нужен токен GitHub с доступом к нему.");
        response.EnsureSuccessStatusCode();
        responseEtag = response.Headers.ETag?.ToString();
        using var stream = response.Content.ReadAsStream(cts.Token);
        return JsonDocument.Parse(stream);
    }

    public void Dispose() => _http.Dispose();

    private static string Describe(HttpRequestException ex)
    {
        if (ex.StatusCode is not null) return ex.Message;
        return ex.Message + " Проверьте подключение к интернету и доступ к github.com.";
    }
}
