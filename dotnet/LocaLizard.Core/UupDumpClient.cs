using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
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
                var package = GetFiles(filesDoc.RootElement).FirstOrDefault(IsClientLp);
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
                    var extracted = await ExtractUpdateMumAsync(target, targetFolder, runner, ct);
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
        if (httpCdn && (package.Sha1?.Length != 40 || package.Size is null or <= 0))
            throw new InvalidDataException("HTTP Microsoft CDN разрешён только при наличии SHA-1 и размера из UUP Dump.");

        var current = package.Url;
        HttpResponseMessage? response = null;
        try
        {
            for (var redirects = 0; redirects <= 8; redirects++)
            {
                response = await _http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    response = null;
                    if (location is null) throw new InvalidDataException("Перенаправление CDN без Location.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!SecurityPolicy.IsTrustedPackageUrl(current))
                        throw new InvalidDataException($"Заблокировано перенаправление CDN на {current.Host}.");
                    if (current.Scheme == Uri.UriSchemeHttp && (!httpCdn || !SecurityPolicy.IsExactMicrosoftCdnHttp(current)))
                        throw new InvalidDataException("Недопустимое HTTP-перенаправление.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                break;
            }
            if (response is null || !SecurityPolicy.IsTrustedPackageUrl(current))
                throw new InvalidDataException("Слишком много перенаправлений или недоверенный финальный URL.");

            var temp = target + ".download";
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            long count = 0;
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.AppendData(buffer, 0, read);
                    count += read;
                }
            }
            catch { try { File.Delete(temp); } catch { } throw; }

            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (count == 0 || package.Size is > 0 && count != package.Size)
            {
                File.Delete(temp);
                throw new InvalidDataException("Размер загрузки не совпадает с метаданными UUP Dump.");
            }
            if (!string.IsNullOrWhiteSpace(package.Sha1) &&
                !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(package.Sha1)))
            {
                File.Delete(temp);
                throw new InvalidDataException("SHA-1 загрузки не совпадает с UUP Dump.");
            }
            File.Move(temp, target, true);
        }
        finally { response?.Dispose(); }
    }

    private static async Task<bool> ExistingFileValidAsync(string path, UupFile package, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || package.Size is > 0 && info.Length != package.Size) return false;
        if (string.IsNullOrWhiteSpace(package.Sha1)) return true;
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct)).ToLowerInvariant();
        return actual.Equals(package.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ExtractUpdateMumAsync(string esd, string folder, ProcessRunner runner, CancellationToken ct)
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
            return Path.Combine(finalDirectory, "update.mum");
        }
        finally { try { Directory.Delete(temporary, true); } catch { } }
    }

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
        if (response.TryGetProperty("error", out var error) && error.GetString() == "SEARCH_NO_RESULTS") return [];
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

    internal static bool IsClientLp(UupFile file)
    {
        var n = file.Name.ToLowerInvariant();
        return n.Contains("ru-ru") && (n.EndsWith(".cab") || n.EndsWith(".esd")) &&
               !new[] { "server", "winpe", "setup", "recovery", "languagefeatures", "local-experience-pack" }.Any(n.Contains) &&
               new[] { "client-language-pack", "client-languagepack", "clientlanguagepack", "language-pack", "languagepack" }.Any(n.Contains);
    }

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private static long? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var item) && long.TryParse(item.ToString(), out var value) ? value : null;

    private static Uri? ReadUri(JsonElement element, string property) =>
        Uri.TryCreate(Text(element, property), UriKind.Absolute, out var uri) ? uri : null;

    public void Dispose() => _http.Dispose();
    internal sealed record UupFile(string Name, Uri? Url, string? Sha1, long? Size);
}
