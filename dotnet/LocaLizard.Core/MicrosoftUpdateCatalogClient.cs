using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record CatalogUpdate(string Id, string Title, string Date, string Size,
    int? BuildFamily, int BuildRevision);

public sealed record CatalogFile(string FileName, Uri Url, string? Sha1, long? Size, string CatalogIndex);

public sealed record CatalogDownloadResult(string Folder, string ManifestPath, string Sha256Path,
    IReadOnlyList<string> Files, int TargetBuild, string Architecture, string KnowledgeBase);

public sealed class MicrosoftUpdateCatalogClient : IDisposable
{
    private const string CatalogBase = "https://www.catalog.update.microsoft.com";
    private const string CatalogHost = "www.catalog.update.microsoft.com";
    private static readonly HashSet<string> AllowedDownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "catalog.s.download.windowsupdate.com",
        "dl.delivery.mp.microsoft.com",
        "download.microsoft.com",
        "download.windowsupdate.com",
        "www.download.windowsupdate.com"
    };

    private readonly HttpClient _client;

    public MicrosoftUpdateCatalogClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(20) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version} (Windows desktop utility)");
        _client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US");
    }

    public async Task<IReadOnlyList<CatalogUpdate>> SearchAsync(string query, string architecture,
        string? knowledgeBase = null, int? targetBuild = null, CancellationToken ct = default,
        Action<string>? log = null)
    {
        architecture = BuildNumbers.Architecture(architecture);
        if (!BuildNumbers.IsSupportedArchitecture(architecture))
            throw new InvalidDataException($"Неподдерживаемая архитектура: {architecture}");
        if (knowledgeBase is not null && !Regex.IsMatch(knowledgeBase, "^\\d{6,8}$"))
            throw new InvalidDataException("Номер KB должен содержать 6–8 цифр.");
        var candidates = new List<string> { query };
        if (targetBuild.HasValue)
        {
            candidates.Add($"Cumulative Update {targetBuild.Value}");
            candidates.Add($"OS Build {targetBuild.Value}");
            candidates.Add("Cumulative Update");
        }

        var attempted = new List<string>();
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var uri = new Uri(CatalogBase + "/Search.aspx?q=" + Uri.EscapeDataString(candidate));
            attempted.Add(uri.ToString());
            log?.Invoke("Microsoft Update Catalog: " + candidate);
            var markup = await FetchCatalogPageAsync(uri, null, null, ct);
            var rows = ParseSearchResults(markup);
            var filtered = FilterResults(rows, architecture, knowledgeBase, targetBuild);
            if (filtered.Count > 0) return filtered;
        }
        throw new InvalidOperationException(
            "В Microsoft Update Catalog не найдено стабильное LCU с заданными KB, семейством сборки и архитектурой. " +
            "Попробуйте указать KB или подготовить updates.json вручную. Запросы: " + string.Join(" | ", attempted));
    }

    public async Task<CatalogDownloadResult> DownloadSelectedAsync(CatalogUpdate selected, string knowledgeBase,
        string architecture, string packageRoot, CancellationToken ct = default, Action<string>? log = null)
    {
        architecture = BuildNumbers.Architecture(architecture);
        if (!BuildNumbers.IsSupportedArchitecture(architecture))
            throw new InvalidDataException("Неизвестная архитектура выбранного ISO.");
        if (selected.BuildFamily is null)
            throw new InvalidDataException("В выбранной записи Catalog не определено семейство сборки.");
        var kb = Regex.Replace(knowledgeBase.Trim(), "(?i)^KB", "");
        if (!Regex.IsMatch(kb, "^\\d{6,8}$"))
            throw new InvalidDataException("Некорректный номер KB.");
        if (!Guid.TryParse(selected.Id, out _))
            throw new InvalidDataException("Microsoft Update Catalog вернул некорректный update ID.");

        var files = await GetDownloadLinksAsync(selected.Id, ct);
        var parsed = new List<(CatalogFile File, string Kb, string Kind)>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file.FileName);
            var extension = Path.GetExtension(name);
            if (!(extension.Equals(".msu", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))) continue;
            var kbMatch = Regex.Match(name, "(?i)kb(\\d{6,8})");
            if (!kbMatch.Success) continue;
            var number = kbMatch.Groups[1].Value;
            var fileArch = FileArchitecture(name);
            if (fileArch is not null && fileArch != architecture) continue;
            var kind = number == kb ? "target_lcu" : "checkpoint";
            if (kind == "checkpoint" && !extension.Equals(".msu", StringComparison.OrdinalIgnoreCase)) continue;
            parsed.Add((file with { FileName = name }, number, kind));
        }

        var targets = parsed.Where(x => x.Kind == "target_lcu").ToArray();
        if (targets.Length != 1)
            throw new InvalidOperationException(
                $"Для KB{kb} ожидался ровно один целевой LCU-пакет, найдено {targets.Length}. Не буду выбирать пакет наугад.");
        var checkpoints = parsed.Where(x => x.Kind == "checkpoint")
            .OrderBy(x => int.Parse(x.Kb, System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(x => x.File.FileName, StringComparer.OrdinalIgnoreCase).ToArray();
        var ordered = checkpoints.Concat(targets).ToArray();

        var folderName = $"KB{kb}_{architecture}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        var folder = Path.Combine(Path.GetFullPath(packageRoot), "catalog", folderName);
        Directory.CreateDirectory(folder);
        var fileRows = new List<object>();
        var manifestRows = new List<object>();
        var createdFiles = new List<string>();
        foreach (var entry in ordered)
        {
            ct.ThrowIfCancellationRequested();
            SecurityPolicy.EnsureSafeFileName(folder, entry.File.FileName);
            var destination = Path.Combine(folder, entry.File.FileName);
            log?.Invoke($"Catalog: загружаю KB{entry.Kb} ({entry.Kind}) — {entry.File.FileName}.");
            var actual = await DownloadAndVerifyAsync(entry.File, destination, ct);
            fileRows.Add(new
            {
                file = entry.File.FileName,
                kb = entry.Kb,
                kind = entry.Kind,
                size = actual.Size,
                sha1_verified = actual.Sha1Verified,
                sha256 = actual.Sha256,
                url = entry.File.Url.ToString()
            });
            manifestRows.Add(new { file = entry.File.FileName, kind = entry.Kind, sha256 = actual.Sha256 });
            createdFiles.Add(destination);
        }

        if (manifestRows.Count == 0 || ordered[^1].Kind != "target_lcu")
            throw new InvalidDataException("Целевой LCU должен быть последним в цепочке обновлений.");
        var manifest = new
        {
            schema_version = 1,
            target_build_family = selected.BuildFamily.Value,
            architecture,
            catalog_query = "KB" + kb,
            catalog_title = selected.Title,
            catalog_update_id = selected.Id,
            ordered_packages = manifestRows,
            hash_note = "SHA-256 вычислен локально после HTTPS-загрузки; SHA-1 Catalog сверялся только если сервер предоставил его."
        };
        var manifestPath = Path.Combine(folder, "updates.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), ct);
        var sourceInfo = new
        {
            source = "Microsoft Update Catalog",
            catalog_search_url = CatalogBase + "/Search.aspx?q=" + Uri.EscapeDataString("KB" + kb),
            selected_title = selected.Title,
            update_id = selected.Id,
            downloaded_at = DateTimeOffset.Now,
            packages = fileRows
        };
        await File.WriteAllTextAsync(Path.Combine(folder, "catalog-source.json"),
            JsonSerializer.Serialize(sourceInfo, new JsonSerializerOptions { WriteIndented = true }), ct);
        var shaRows = ordered.Select((entry, index) =>
        {
            var path = createdFiles[index];
            return $"{GetSha256(path)} *{Path.GetFileName(path)}";
        });
        var shaPath = Path.Combine(folder, "SHA256.txt");
        await File.WriteAllTextAsync(shaPath, string.Join(Environment.NewLine, shaRows) + Environment.NewLine, Encoding.ASCII, ct);
        return new CatalogDownloadResult(folder, manifestPath, shaPath, createdFiles,
            selected.BuildFamily.Value, architecture, kb);
    }

    public static string QueryForBuild(int build, string architecture)
    {
        var versions = new Dictionary<int, string>
        {
            [19041] = "Windows 10 Version 2004", [19042] = "Windows 10 Version 20H2",
            [19043] = "Windows 10 Version 21H1", [19044] = "Windows 10 Version 21H2",
            [19045] = "Windows 10 Version 22H2", [22000] = "Windows 11, version 21H2",
            [22621] = "Windows 11, version 22H2", [22631] = "Windows 11, version 23H2",
            [26100] = "Windows 11, version 24H2", [26200] = "Windows 11, version 25H2",
            [26300] = "Windows 11, version 26H2"
        };
        architecture = BuildNumbers.Architecture(architecture);
        if (!BuildNumbers.IsSupportedArchitecture(architecture))
            throw new InvalidDataException("Неподдерживаемая архитектура для Microsoft Update Catalog.");
        var version = versions.TryGetValue(build, out var value) ? value : $"Windows OS Build {build}";
        return $"Cumulative Update for {version} for {architecture}-based Systems";
    }

    private async Task<IReadOnlyList<CatalogFile>> GetDownloadLinksAsync(string updateId, CancellationToken ct)
    {
        var requestJson = JsonSerializer.Serialize(new[]
        {
            new { size = 0, languages = "", uidInfo = updateId, updateID = updateId }
        });
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["updateIDs"] = requestJson });
        var markup = await FetchCatalogPageAsync(new Uri(CatalogBase + "/DownloadDialog.aspx"), form,
            new Uri(CatalogBase + "/Search.aspx"), ct);
        var links = ExtractDownloads(markup);
        if (links.Count == 0)
            throw new InvalidOperationException("Каталог не вернул прямые ссылки MSU/CAB; формат DownloadDialog, вероятно, изменился.");
        return links;
    }

    private async Task<string> FetchCatalogPageAsync(Uri uri, HttpContent? data, Uri? referer, CancellationToken ct)
    {
        if (!SecurityPolicy.IsCatalogUrl(uri)) throw new InvalidDataException("Отклонён адрес вне Microsoft Update Catalog.");
        for (var attempt = 0; attempt < 6; attempt++)
        {
            using var request = new HttpRequestMessage(data is null ? HttpMethod.Get : HttpMethod.Post, uri);
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            if (referer is not null) request.Headers.Referrer = referer;
            if (data is not null) request.Content = new FormUrlEncodedContent(await ReadFormContentAsync(data, ct));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location ?? throw new InvalidDataException("Каталог вернул redirect без Location.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!SecurityPolicy.IsCatalogUrl(uri))
                    throw new InvalidDataException("Microsoft Update Catalog перенаправил запрос на посторонний домен.");
                if (response.StatusCode == HttpStatusCode.SeeOther || response.StatusCode == HttpStatusCode.Found ||
                    response.StatusCode == HttpStatusCode.Moved)
                {
                    data = null;
                }
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > 12 * 1024 * 1024)
                throw new InvalidDataException("Ответ Microsoft Update Catalog превышает 12 MiB.");
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 12 * 1024 * 1024)
                throw new InvalidDataException("Ответ Microsoft Update Catalog превышает 12 MiB.");
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        throw new InvalidOperationException("Слишком много перенаправлений Microsoft Update Catalog.");
    }

    private static async Task<Dictionary<string, string>> ReadFormContentAsync(HttpContent content, CancellationToken ct)
    {
        var raw = await content.ReadAsStringAsync(ct);
        return raw.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "",
                StringComparer.Ordinal);
    }

    private static IReadOnlyList<CatalogUpdate> ParseSearchResults(string markup)
    {
        var results = new List<CatalogUpdate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match row in Regex.Matches(markup ?? "", "<tr\\b[^>]*>.*?</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var html = row.Value;
            if (!Regex.IsMatch(html, "goToDetails|updateID|type\\s*=\\s*['\"]checkbox|checkbox", RegexOptions.IgnoreCase)) continue;
            var cells = Regex.Matches(html, "<td\\b[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(m => HtmlText(m.Groups[1].Value)).ToArray();
            if (cells.Length < 2) continue;
            var title = cells[1];
            if (string.IsNullOrWhiteSpace(title) || title.Equals("Title", StringComparison.OrdinalIgnoreCase)) continue;
            var id = Regex.Match(html, "goToDetails\\s*\\(\\s*['\"]?([0-9a-f-]{36})", RegexOptions.IgnoreCase);
            if (!id.Success) id = Regex.Match(html, "(?:id|value)\\s*=\\s*['\"]([0-9a-f-]{36})['\"]", RegexOptions.IgnoreCase);
            if (!id.Success) continue;
            var updateId = id.Groups[1].Value.ToLowerInvariant();
            if (!seen.Add(updateId)) continue;
            var build = Regex.Match(title, "\\((\\d{5})\\.(\\d+)\\)");
            if (!build.Success) build = Regex.Match(title, "OS\\s+Build(?:s)?\\s*[:( ]+(\\d{5})\\.(\\d+)", RegexOptions.IgnoreCase);
            int? family = build.Success && int.TryParse(build.Groups[1].Value, out var parsedFamily) ? parsedFamily : null;
            var revision = build.Success && int.TryParse(build.Groups[2].Value, out var parsedRevision) ? parsedRevision : 0;
            results.Add(new CatalogUpdate(updateId, title, cells.Length > 4 ? cells[4] : "",
                cells.Length > 6 ? cells[6] : "", family, revision));
        }
        return results;
    }

    private static IReadOnlyList<CatalogUpdate> FilterResults(IReadOnlyList<CatalogUpdate> rows, string architecture,
        string? knowledgeBase, int? targetBuild)
    {
        var stable = rows.Where(row =>
        {
            var title = row.Title;
            var low = title.ToLowerInvariant();
            if (!ArchitectureMatches(title, architecture)) return false;
            if (knowledgeBase is not null && !Regex.IsMatch(title, "\\bKB\\s*" + Regex.Escape(knowledgeBase) + "\\b", RegexOptions.IgnoreCase)) return false;
            if (targetBuild.HasValue && row.BuildFamily != targetBuild.Value) return false;
            if (!low.Contains("cumulative update") && !low.Contains("out-of-band update")) return false;
            if (new[] { "preview", "insider", "canary", "beta channel", "dev channel", "dynamic update", "server", ".net framework", "defender" }.Any(low.Contains)) return false;
            return low.Contains("windows 10") || low.Contains("windows 11");
        }).OrderByDescending(x => x.BuildRevision).ThenByDescending(x => x.BuildFamily)
          .ThenByDescending(x => x.Date, StringComparer.OrdinalIgnoreCase)
          .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Take(40).ToArray();
        return stable;
    }

    private static IReadOnlyList<CatalogFile> ExtractDownloads(string markup)
    {
        var found = new List<CatalogFile>();
        foreach (Match match in Regex.Matches(markup ?? "",
                     "\\[(?<idx>\\d+)\\]\\.url\\s*=\\s*['\"](?<url>https?://[^'\"]+)['\"]",
                     RegexOptions.IgnoreCase))
        {
            var index = match.Groups["idx"].Value;
            var urlText = WebUtility.HtmlDecode(match.Groups["url"].Value).Replace("\\/", "/");
            if (!Uri.TryCreate(urlText, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host.ToLowerInvariant();
            if (uri.Scheme == Uri.UriSchemeHttp && AllowedDownloadHosts.Contains(host))
                uri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
            if (!IsSafeDownloadUrl(uri)) continue;
            var fileMatch = Regex.Match(markup,
                "\\[" + Regex.Escape(index) + "\\]\\.fileName\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            var hashMatch = Regex.Match(markup,
                "\\[" + Regex.Escape(index) + "\\]\\.digest\\s*=\\s*['\"]([^'\"]+)['\"]", RegexOptions.IgnoreCase);
            var sizeMatch = Regex.Match(markup,
                "\\[" + Regex.Escape(index) + "\\]\\.(?:size|fileSize)\\s*=\\s*['\"]?(\\d+)", RegexOptions.IgnoreCase);
            var fileName = fileMatch.Success ? WebUtility.HtmlDecode(fileMatch.Groups[1].Value).Trim()
                : Path.GetFileName(uri.AbsolutePath) ?? "";
            fileName = Path.GetFileName(fileName.Replace('\\', '/'));
            var extension = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(fileName) ||
                !(extension.Equals(".msu", StringComparison.OrdinalIgnoreCase) ||
                  extension.Equals(".cab", StringComparison.OrdinalIgnoreCase))) continue;
            var rawHash = hashMatch.Success ? hashMatch.Groups[1].Value.Trim() : "";
            var sha = Regex.Match(rawHash, "(?i)^(?:sha1[:=])?([0-9a-f]{40})$");
            long? size = sizeMatch.Success && long.TryParse(sizeMatch.Groups[1].Value, out var parsedSize) ? parsedSize : null;
            var item = new CatalogFile(fileName, uri, sha.Success ? sha.Groups[1].Value.ToLowerInvariant() : null, size, index);
            if (!found.Any(x => x.Url == uri)) found.Add(item);
        }
        return found;
    }

    private async Task<(string Sha256, long Size, bool Sha1Verified)> DownloadAndVerifyAsync(
        CatalogFile file, string destination, CancellationToken ct)
    {
        var current = file.Url;
        if (!IsSafeDownloadUrl(current)) throw new InvalidDataException("URL обновления не входит в список доверенных Microsoft CDN.");
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var temp = destination + ".download";
        long total = 0;
        try
        {
            using var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            for (var redirects = 0; redirects < 6; redirects++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location ?? throw new InvalidDataException("CDN redirect без Location.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!IsSafeDownloadUrl(current))
                        throw new InvalidDataException($"Заблокировано перенаправление на {current.Host}.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (!IsSafeDownloadUrl(response.RequestMessage?.RequestUri ?? current))
                    throw new InvalidDataException("Финальный URL загрузки не доверенный.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha1.AppendData(buffer, 0, read);
                    sha256.AppendData(buffer, 0, read);
                    total += read;
                }
                break;
            }
            await output.FlushAsync(ct);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
            throw;
        }
        var actualSha1 = Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant();
        var actualSha256 = Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        if (total <= 0 || file.Size.HasValue && total != file.Size.Value)
        {
            File.Delete(temp);
            throw new InvalidDataException($"Размер {file.FileName} не совпадает с метаданными Catalog.");
        }
        var shaVerified = false;
        if (!string.IsNullOrWhiteSpace(file.Sha1))
        {
            var expected = Convert.FromHexString(file.Sha1);
            var actual = Convert.FromHexString(actualSha1);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                File.Delete(temp);
                throw new InvalidDataException($"SHA-1 {file.FileName} не совпадает с Catalog.");
            }
            shaVerified = true;
        }
        File.Move(temp, destination, true);
        return (actualSha256, total, shaVerified);
    }

    private static bool ArchitectureMatches(string title, string architecture)
    {
        var low = title.ToLowerInvariant();
        return architecture switch
        {
            "x64" => Regex.IsMatch(low, "\\bx64[- ]based systems\\b|\\bx64\\b|\\bamd64\\b"),
            "arm64" => Regex.IsMatch(low, "\\barm64[- ]based systems\\b|\\barm64\\b|\\baarch64\\b"),
            "x86" => Regex.IsMatch(low, "\\bx86[- ]based systems\\b|\\bx86\\b"),
            _ => false
        };
    }

    private static string? FileArchitecture(string file)
    {
        var match = Regex.Match(Path.GetFileName(file), "(?i)(?:^|[-_.])(x64|amd64|arm64|x86)(?:[-_.]|$)");
        if (!match.Success) return null;
        var value = match.Groups[1].Value.ToLowerInvariant();
        return value == "amd64" ? "x64" : value;
    }

    private static bool IsSafeDownloadUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        AllowedDownloadHosts.Contains(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    private static string HtmlText(string value)
    {
        var noScript = Regex.Replace(value, "<(script|style)\\b[^>]*>.*?</\\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var stripped = Regex.Replace(noScript, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(stripped), "\\s+", " ").Trim();
    }

    private static string GetSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose() => _client.Dispose();
}
