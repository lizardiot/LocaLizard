using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed class UupDumpClient : IDisposable
{
    private const string Api = "https://api.uupdump.net";
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false });

    public UupDumpClient()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
        _http.Timeout = TimeSpan.FromSeconds(45);
    }

    public async Task<IReadOnlyList<string>> DownloadRussianLanguagePackAsync(
        ImageInfo image, string cache, ProcessRunner runner, Action<string>? log, CancellationToken ct)
    {
        var family = image.BuildFamily;
        var arch = image.NormalizedArchitecture;
        if (family == 0 || !BuildNumbers.IsSupportedArchitecture(arch))
            throw new InvalidOperationException($"Сборка/архитектура образа не распознаны: {image.Version}/{image.Architecture}");

        using var listDoc = await GetApiDocumentAsync($"{Api}/listid.php?search={family}&sortByDate=1", true, ct);
        var builds = GetBuilds(listDoc.RootElement)
            .Where(b => CandidateMatches(b, family, arch))
            .Take(32).ToArray();

        if (builds.Length == 0) throw new InvalidOperationException($"UUP Dump не вернул клиентские сборки Windows {family}/{arch}.");

        foreach (var build in builds)
        {
            ct.ThrowIfCancellationRequested();
            var id = Text(build, "uuid");
            var title = Text(build, "title");
            try
            {
                using var langsDoc = await GetApiDocumentAsync($"{Api}/listlangs.php?id={Uri.EscapeDataString(id)}", true, ct);
                if (!GetLanguages(langsDoc.RootElement).Contains("ru-ru", StringComparer.OrdinalIgnoreCase)) continue;

                using var filesDoc = await GetApiDocumentAsync($"{Api}/get.php?id={Uri.EscapeDataString(id)}&lang=ru-ru", false, ct);
                var package = GetFiles(filesDoc.RootElement).FirstOrDefault(x => IsClientLp(x, arch));
                if (package is null) continue;

                var targetFolder = Path.Combine(cache, "auto", $"{family}_{arch}");
                Directory.CreateDirectory(targetFolder);
                var fileName = Path.GetFileName(package.Name);
                SecurityPolicy.EnsureSafeFileName(targetFolder, fileName);
                var target = Path.Combine(targetFolder, fileName);
                if (!await ExistingFileValidAsync(target, package, ct))
                    await DownloadAsync(package, target, ct);

                if (target.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
                {
                    var extracted = await ExtractUpdateMumAsync(target, targetFolder, package, runner, ct);
                    log?.Invoke($"Извлечён проверенный Client Language Pack ESD: {title}");
                    return [extracted];
                }
                log?.Invoke($"Загружен проверенный Client Language Pack CAB: {title}");
                return [target];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke($"Кандидат UUP {title} пропущен: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Не удалось получить совместимый Client Language Pack ru-RU из UUP Dump.");
    }

    private async Task<JsonDocument> GetApiDocumentAsync(string url, bool searchNoResultsIsEmpty, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            if (!(searchNoResultsIsEmpty && response.StatusCode == HttpStatusCode.BadRequest && IsSearchNoResults(payload)))
                response.EnsureSuccessStatusCode();
        }
        return JsonDocument.Parse(payload);
    }

    private static bool IsSearchNoResults(string payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("response", out var response) &&
                   response.ValueKind == JsonValueKind.Object &&
                   response.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.String &&
                   string.Equals(error.GetString(), "SEARCH_NO_RESULTS", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    public async Task<string> DiagnoseAsync(CancellationToken ct)
    {
        var families = new (string Name, int Build)[]
        {
            ("Windows 11 IoT Enterprise LTSC 2024", 26100),
            ("Windows 10 IoT Enterprise LTSC 2021", 19044),
            ("Windows 10 IoT Enterprise LTSC 2019", 17763),
            ("Windows 10 IoT Enterprise LTSC 2016", 14393),
            ("Windows 10 IoT Enterprise LTSC 2015", 10240)
        };
        var report = new List<string> { "Проверка доступности клиентских сборок UUP Dump:" };
        foreach (var family in families)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var document = await GetApiDocumentAsync(
                    $"{Api}/listid.php?search={family.Build}&sortByDate=1", true, ct);
                var builds = GetBuilds(document.RootElement)
                    .Where(x => CandidateMatchesAnyArchitecture(x, family.Build))
                    .ToArray();
                var architectures = builds.Select(x => NormalizeArchitecture(Text(x, "arch")))
                    .Where(BuildNumbers.IsSupportedArchitecture)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
                report.Add($"{family.Name} (сборка {family.Build}): {builds.Length} записей; архитектуры: " +
                           (string.Join(", ", architectures) is { Length: > 0 } value ? value : "нет"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report.Add($"{family.Name} (сборка {family.Build}): ошибка запроса — {ex.Message}"); }
        }
        return string.Join(Environment.NewLine, report);
    }

    private static bool CandidateMatchesAnyArchitecture(JsonElement build, int family) =>
        new[] { "x64", "x86", "arm64" }.Any(arch => CandidateMatches(build, family, arch));

    private static string NormalizeArchitecture(string architecture) => architecture.ToLowerInvariant() switch
    {
        "amd64" or "x86_64" => "x64",
        "aarch64" => "arm64",
        var value => value
    };

    private async Task DownloadAsync(UupFile package, string target, CancellationToken ct)
    {
        if (package.Url is null || !SecurityPolicy.IsTrustedPackageUrl(package.Url))
            throw new InvalidDataException("URL языкового пакета не прошёл проверку домена.");
        var httpCdn = SecurityPolicy.IsExactMicrosoftCdnHttp(package.Url);
        if (httpCdn && (!Regex.IsMatch(package.Sha1 ?? "", "^[0-9a-fA-F]{40}$") || package.Size is null or <= 0))
            throw new InvalidDataException("HTTP Microsoft CDN разрешён только при наличии SHA-1 и размера из UUP Dump.");

        Exception? last = null;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DownloadAttemptAsync(package, target, httpCdn, ct);
                return;
            }
            catch (Exception ex) when (IsRetryable(ex, ct) && attempt < 3)
            {
                last = ex;
                var delay = TimeSpan.FromMilliseconds(Math.Min(8000, 1500 * Math.Pow(2, attempt)));
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (IsRetryable(ex, ct))
            {
                last = ex;
                break;
            }
        }
        throw new IOException($"Не удалось загрузить {Path.GetFileName(target)} после повторных попыток.", last);
    }

    private async Task DownloadAttemptAsync(UupFile package, string target, bool initialHttpCdn, CancellationToken ct)
    {
        var temp = target + ".download";
        var offset = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        var current = package.Url!;
        HttpResponseMessage? response = null;
        try
        {
            for (var redirects = 0; redirects <= 8; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (offset > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, null);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    response = null;
                    if (location is null) throw new InvalidDataException("Перенаправление CDN без Location.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!SecurityPolicy.IsTrustedPackageUrl(current))
                        throw new InvalidDataException($"Заблокировано перенаправление CDN на {current.Host}.");
                    if (current.Scheme == Uri.UriSchemeHttp && (!initialHttpCdn || !SecurityPolicy.IsExactMicrosoftCdnHttp(current)))
                        throw new InvalidDataException("Недопустимое HTTP-перенаправление.");
                    continue;
                }
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    response.Dispose();
                    response = null;
                    try { File.Delete(temp); } catch { }
                    throw new IOException("CDN отклонил диапазон продолжения; повторяю загрузку с начала.");
                }
                response.EnsureSuccessStatusCode();
                break;
            }
            if (response is null || !SecurityPolicy.IsTrustedPackageUrl(current))
                throw new InvalidDataException("Слишком много перенаправлений или недоверенный финальный URL.");
            if (current.Scheme == Uri.UriSchemeHttp && !initialHttpCdn)
                throw new InvalidDataException("Финальный HTTP URL разрешён только для точного CDN пути Microsoft с SHA-1 и размером.");

            var append = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent &&
                         response.Content.Headers.ContentRange?.From == offset;
            if (offset > 0 && response.StatusCode == HttpStatusCode.PartialContent && !append)
            {
                try { File.Delete(temp); } catch { }
                throw new InvalidDataException("CDN вернул неверный Content-Range для продолжения загрузки.");
            }
            if (offset > 0 && !append)
            {
                try { File.Delete(temp); } catch { }
            }

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(temp, append ? FileMode.OpenOrCreate : FileMode.Create,
                FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                if (append) output.Position = output.Length;
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                await output.FlushAsync(ct);
            }

            var info = new FileInfo(temp);
            if (info.Length == 0 || package.Size is > 0 && info.Length != package.Size)
            {
                File.Delete(temp);
                throw new InvalidDataException("Размер загрузки не совпадает с метаданными UUP Dump.");
            }
            string actualSha1;
            await using (var stream = File.OpenRead(temp))
                actualSha1 = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(package.Sha1))
            {
                if (!Regex.IsMatch(package.Sha1, "^[0-9a-fA-F]{40}$") ||
                    !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualSha1), Convert.FromHexString(package.Sha1)))
                {
                    File.Delete(temp);
                    throw new InvalidDataException("SHA-1 загрузки не совпадает с UUP Dump.");
                }
            }
            File.Move(temp, target, true);
            await RecordCacheFileAsync(target, package, current, ct);
        }
        finally { response?.Dispose(); }
    }

    private static bool IsRetryable(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;
        if (ex is OperationCanceledException) return true; // HttpClient timeout, not caller cancellation
        if (ex is HttpRequestException http)
        {
            var status = (int?)http.StatusCode;
            return status is null || status == 408 || status == 416 || status == 429 || status >= 500;
        }
        return ex is IOException or TimeoutException;
    }

    private static async Task<bool> ExistingFileValidAsync(string path, UupFile package, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || package.Size is > 0 && info.Length != package.Size) return false;
        if (!await VerifyManagedCacheFileAsync(path, ct)) return false;
        if (string.IsNullOrWhiteSpace(package.Sha1)) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
        return actual.Equals(package.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task<bool> VerifyManagedCacheFileAsync(string path, CancellationToken ct)
    {
        var cacheRoot = GetManagedCacheRoot(path);
        if (cacheRoot is null) return File.Exists(path);
        var manifestPath = Path.Combine(cacheRoot, "package_manifest.json");
        if (!File.Exists(manifestPath) || !File.Exists(path)) return false;
        try
        {
            var manifest = JsonSerializer.Deserialize<CacheManifest>(
                await File.ReadAllTextAsync(manifestPath, ct),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var relative = Path.GetRelativePath(cacheRoot, Path.GetFullPath(path)).Replace('\\', '/');
            if (manifest is null || manifest.SchemaVersion != 1 ||
                !manifest.Packages.TryGetValue(relative, out var record)) return false;
            var info = new FileInfo(path);
            if (info.Length != record.Size || record.Size <= 0) return false;
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            return actual.Equals(record.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }

    private static async Task RecordCacheFileAsync(string path, UupFile package, Uri source, CancellationToken ct)
    {
        var cacheRoot = GetManagedCacheRoot(path);
        if (cacheRoot is null) return;
        var info = new FileInfo(path);
        await using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        await using var shaStream = File.OpenRead(path);
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(shaStream, ct)).ToLowerInvariant();
        await SaveCacheRecordAsync(cacheRoot, path, new CacheRecord(info.Length, sha1, sha256,
            package.Sha1, source.Host, DateTimeOffset.UtcNow), ct);
    }

    private static async Task RecordExtractedDirectoryAsync(string directory, string cacheRoot, UupFile package, CancellationToken ct)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            await using var stream = File.OpenRead(file);
            var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            await using var shaStream = File.OpenRead(file);
            var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(shaStream, ct)).ToLowerInvariant();
            await SaveCacheRecordAsync(cacheRoot, file, new CacheRecord(info.Length, sha1, sha256,
                package.Sha1, package.Url?.Host, DateTimeOffset.UtcNow), ct);
        }
    }

    private static async Task SaveCacheRecordAsync(string cacheRoot, string path, CacheRecord record, CancellationToken ct)
    {
        var manifestPath = Path.Combine(cacheRoot, "package_manifest.json");
        CacheManifest manifest;
        try
        {
            manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<CacheManifest>(await File.ReadAllTextAsync(manifestPath, ct),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CacheManifest()
                : new CacheManifest();
        }
        catch (JsonException) { manifest = new CacheManifest(); }
        var relative = Path.GetRelativePath(cacheRoot, Path.GetFullPath(path)).Replace('\\', '/');
        manifest.SchemaVersion = 1;
        manifest.Packages[relative] = record;
        var temporary = manifestPath + ".tmp";
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        File.Move(temporary, manifestPath, true);
    }

    private static string? GetManagedCacheRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var parts = full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(parts, x => x.Equals("auto", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= parts.Length) return null;
        DirectoryInfo? current = new(Path.GetDirectoryName(full)!);
        while (current is not null)
        {
            if (current.Parent?.Name.Equals("auto", StringComparison.OrdinalIgnoreCase) == true)
                return current.FullName;
            current = current.Parent;
        }
        return null;
    }

    private static async Task<string> ExtractUpdateMumAsync(string esd, string folder, UupFile package, ProcessRunner runner, CancellationToken ct)
    {
        var temporary = Path.Combine(folder, "esd_extract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            await runner.RunAsync("dism.exe",
                ["/Apply-Image", $"/ImageFile:{esd}", "/Index:1", $"/ApplyDir:{temporary}", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(40));
            var mum = Directory.EnumerateFiles(temporary, "update.mum", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.Contains("WinSxS", StringComparison.OrdinalIgnoreCase));
            if (mum is null) throw new InvalidDataException("В ESD не найден Client Language Pack update.mum.");
            var packageDirectory = Path.GetDirectoryName(mum)!;
            var finalDirectory = Path.Combine(folder, Path.GetFileNameWithoutExtension(esd) + "_extracted");
            if (Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, true);
            Directory.Move(packageDirectory, finalDirectory);
            await RecordExtractedDirectoryAsync(finalDirectory, folder, package, ct);
            return Path.Combine(finalDirectory, "update.mum");
        }
        finally { try { Directory.Delete(temporary, true); } catch { } }
    }

    private sealed class CacheManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("packages")]
        public Dictionary<string, CacheRecord> Packages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record CacheRecord(
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("api_sha1")] string? ApiSha1,
        [property: JsonPropertyName("source_host")] string? SourceHost,
        [property: JsonPropertyName("downloaded_at")] DateTimeOffset DownloadedAt);

    internal static IEnumerable<JsonElement> GetBuilds(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ответ UUP Dump не содержит response.");
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String &&
            error.GetString() is { Length: > 0 } message)
        {
            if (message == "SEARCH_NO_RESULTS") return [];
            throw new InvalidDataException("UUP Dump API: " + message);
        }
        if (!response.TryGetProperty("builds", out var builds))
            throw new InvalidDataException("UUP Dump response.builds отсутствует.");
        var rows = builds.ValueKind switch
        {
            JsonValueKind.Array => builds.EnumerateArray().ToArray(),
            JsonValueKind.Object => builds.EnumerateObject().Select(p => p.Value).ToArray(),
            _ => throw new InvalidDataException("response.builds имеет неизвестный формат.")
        };
        if (rows.Any(x => x.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException("UUP Dump response.builds содержит некорректные элементы.");
        return rows;
    }

    internal static IReadOnlyList<string> GetLanguages(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) || response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Ответ UUP Dump не содержит response.");
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && error.GetString() == "SEARCH_NO_RESULTS") return [];
        if (!response.TryGetProperty("langList", out var value))
            throw new InvalidDataException("UUP Dump response.langList отсутствует.");
        var items = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToArray(),
            JsonValueKind.Object => value.EnumerateObject().Select(p => p.Value).ToArray(),
            _ => []
        };
        return items.Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!.Trim().ToLowerInvariant()).ToArray();
    }

    internal static IReadOnlyList<UupFile> GetFiles(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response) ||
            !response.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("UUP Dump не вернул список файлов.");
        return files.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.Object)
            .Select(p => new UupFile(p.Name, ReadUri(p.Value, "url"), Text(p.Value, "sha1"), Number(p.Value, "size")))
            .ToArray();
    }

    internal static bool CandidateMatches(JsonElement build, int family, string arch)
    {
        var title = Text(build, "title").ToLowerInvariant();
        var foundArch = Text(build, "arch").ToLowerInvariant() switch
        {
            "amd64" or "x86_64" => "x64",
            "aarch64" => "arm64",
            var value => value
        };
        var buildText = Text(build, "build");
        var nums = Regex.Matches(buildText, @"\d+").Select(m => m.Value).ToArray();
        var foundFamily = 0;
        if (nums.Length >= 3 && buildText.StartsWith("10.0.", StringComparison.Ordinal))
            int.TryParse(nums[2], out foundFamily);
        else if (nums.Length > 0)
            int.TryParse(nums[0], out foundFamily);
        return foundFamily == family && foundArch == arch &&
               (title.Contains("windows 10") || title.Contains("windows 11")) &&
               title.Contains("version") &&
               !new[] { "server", "azure stack", "hci", "insider", "canary", "dev channel", "beta channel", "cumulative update", "servicing stack", "dynamic update" }.Any(title.Contains);
    }

    internal static bool IsClientLp(UupFile file, string architecture) =>
        (file.Name.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) ||
         file.Name.EndsWith(".esd", StringComparison.OrdinalIgnoreCase)) &&
        WindowsImageService.IsClientLpName(file.Name, architecture);

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var item) && long.TryParse(item.ToString(), out var value) ? value : null;

    private static Uri? ReadUri(JsonElement element, string property) =>
        Uri.TryCreate(Text(element, property), UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose() => _http.Dispose();
    internal sealed record UupFile(string Name, Uri? Url, string? Sha1, long? Size);
}
