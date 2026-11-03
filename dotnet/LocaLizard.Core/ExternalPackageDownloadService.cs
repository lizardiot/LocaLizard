using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record ExternalPackageDownloadResult(
    string FilePath,
    string FileName,
    long Size,
    string Sha1,
    string Sha256,
    Uri FinalUri);

/// <summary>
/// Downloads only installable package/media files from an explicitly selected source.
/// Community downloads require an independently supplied digest; Microsoft media URLs
/// are restricted to the pinned WhatTheHack references and Microsoft download hosts.
/// </summary>
public sealed class ExternalPackageDownloadService : IDisposable
{
    private const int MaximumRedirects = 5;
    private const long MaximumIsoBytes = 16L * 1024 * 1024 * 1024;
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private readonly HttpClient _http;

    public ExternalPackageDownloadService(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public async Task<ExternalPackageDownloadResult> DownloadAsync(
        string sourceId,
        string sourceUrl,
        string downloadRoot,
        string? expectedDigest,
        CancellationToken ct,
        Action<string>? log = null)
    {
        var source = LanguageSourceCatalog.Find(sourceId)
            ?? throw new InvalidDataException("Неизвестный источник загрузки.");
        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out var initialUri) ||
            !SecurityPolicy.IsPermittedExternalDownloadUrl(sourceId, initialUri))
            throw new InvalidDataException("URL не разрешён для выбранного источника. Нужна прямая HTTPS-ссылка с доверенного домена.");

        if (!string.IsNullOrWhiteSpace(expectedDigest))
        {
            expectedDigest = expectedDigest.Trim().ToLowerInvariant();
            if (!Regex.IsMatch(expectedDigest, @"^(?:[a-f0-9]{40}|[a-f0-9]{64})$"))
                throw new InvalidDataException("Контрольная сумма должна быть SHA-1 (40 символов) или SHA-256 (64 символа).");
        }

        var current = initialUri;
        HttpResponseMessage? response = null;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location;
                response.Dispose();
                response = null;
                if (location is null)
                    throw new InvalidDataException("Сервер вернул redirect без адреса Location.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!SecurityPolicy.IsPermittedExternalDownloadRedirect(sourceId, initialUri, next))
                    throw new InvalidDataException("Перенаправление ведёт на недоверенный домен или протокол и было остановлено.");
                current = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            break;
        }

        if (response is null)
            throw new InvalidDataException("Превышен лимит перенаправлений при загрузке.");
        using (response)
        {
            var finalUri = response.RequestMessage?.RequestUri
                ?? throw new InvalidDataException("Не удалось определить конечный URL загрузки.");
            if (!SecurityPolicy.IsPermittedExternalDownloadRedirect(sourceId, initialUri, finalUri) &&
                !SecurityPolicy.IsPermittedExternalDownloadUrl(sourceId, finalUri))
                throw new InvalidDataException("Конечный URL загрузки не прошёл проверку доверенного домена.");

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is "text/html" or "application/json" or "text/plain")
                throw new InvalidDataException("Сервер вернул веб-страницу/текст вместо установочного файла.");

            var rawName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName
                ?? Path.GetFileName(finalUri.AbsolutePath);
            var fileName = Path.GetFileName(Uri.UnescapeDataString((rawName ?? "").Trim().Trim('"')));
            if (string.IsNullOrWhiteSpace(fileName))
                throw new InvalidDataException("Сервер не сообщил имя файла; URL должен содержать имя файла.");
            SecurityPolicy.EnsureSafeFileName(downloadRoot, fileName);
            if (!ManualPackageImportService.IsSupportedImportExtension(fileName))
                throw new InvalidDataException($"Формат «{Path.GetExtension(fileName)}» не поддерживается. Допустимы CAB, ESD, ISO, APPX/MSIX и License.xml.");

            var isIso = Path.GetExtension(fileName).Equals(".iso", StringComparison.OrdinalIgnoreCase);
            var maximumBytes = isIso ? MaximumIsoBytes : MaximumPackageBytes;
            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is <= 0 || declaredLength > maximumBytes)
                throw new InvalidDataException("Размер загрузки не указан или превышает допустимый предел.");

            string? metadataSha1 = null;
            string? metadataSha256 = null;
            long? metadataSize = null;
            if (sourceId.Equals("fydexx-rs5", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = await new FydexxRs5MetadataProvider().FindAsync(fileName, ct)
                    ?? throw new InvalidDataException("Точное имя файла отсутствует в закреплённом каталоге fydexx.");
                if (metadata.BuildFamily != 17763 || metadata.Sha1 is null ||
                    !fileName.Contains("_ru-ru.", StringComparison.OrdinalIgnoreCase) ||
                    !Path.GetExtension(fileName).Equals(".cab", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("fydexx подтвердил не тот тип/сборку/язык файла.");
                metadataSha1 = metadata.Sha1;
            }
            else if (sourceId.Equals("rg-adguard-files", StringComparison.OrdinalIgnoreCase))
            {
                var metadata = await new RgAdguardFileMetadataProvider().FindAsync(fileName, ct)
                    ?? throw new InvalidDataException("RG-Adguard не вернул метаданные для точного имени файла.");
                metadataSha1 = metadata.Sha1;
                metadataSha256 = metadata.Sha256;
                metadataSize = metadata.Size;
                if (metadataSha1 is null && metadataSha256 is null)
                    throw new InvalidDataException("RG-Adguard не предоставил контрольную сумму.");
            }
            else if (source.Kind == LanguageSourceKind.ManualImport && string.IsNullOrWhiteSpace(expectedDigest))
            {
                throw new InvalidDataException("Для прямой загрузки с community-источника обязательно укажите SHA-1 или SHA-256 из независимого каталога.");
            }

            if (declaredLength is long knownLength && metadataSize is long knownSize && knownLength != knownSize)
                throw new InvalidDataException("Размер загрузки не совпадает с RG-Adguard metadata.");

            var directory = Path.Combine(Path.GetFullPath(downloadRoot), "ManualImport", "Downloads",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, fileName);
            try
            {
                var digest = await SaveAndHashAsync(response, destination, maximumBytes, ct, log);
                if (declaredLength is long expectedLength && digest.Size != expectedLength)
                    throw new InvalidDataException("Фактический размер загрузки не совпал с Content-Length.");
                if (metadataSize is long expectedMetadataSize && digest.Size != expectedMetadataSize)
                    throw new InvalidDataException("Фактический размер не совпал с RG-Adguard metadata.");
                VerifyDigest(digest.Sha1, metadataSha1, "SHA-1 из fydexx/RG-Adguard");
                VerifyDigest(digest.Sha256, metadataSha256, "SHA-256 из RG-Adguard");
                if (!string.IsNullOrWhiteSpace(expectedDigest))
                {
                    var actual = expectedDigest.Length == 40 ? digest.Sha1 : digest.Sha256;
                    VerifyDigest(actual, expectedDigest, "введённая контрольная сумма");
                }

                log?.Invoke($"Загрузка проверена: {fileName}; {digest.Size:N0} байт; SHA-256={digest.Sha256}");
                return new ExternalPackageDownloadResult(
                    destination, fileName, digest.Size, digest.Sha1, digest.Sha256, finalUri);
            }
            catch
            {
                try { if (File.Exists(destination)) File.Delete(destination); } catch { }
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
                throw;
            }
        }
    }

    private static async Task<(long Size, string Sha1, string Sha256)> SaveAndHashAsync(
        HttpResponseMessage response,
        string destination,
        long maximumBytes,
        CancellationToken ct,
        Action<string>? log)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        long nextLog = 256L * 1024 * 1024;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total = checked(total + read);
            if (total > maximumBytes)
                throw new InvalidDataException("Загрузка превысила допустимый размер файла.");
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            if (total >= nextLog)
            {
                log?.Invoke($"Получено {total:N0} байт…");
                nextLog += 256L * 1024 * 1024;
            }
        }
        await output.FlushAsync(ct);
        if (total == 0) throw new InvalidDataException("Сервер вернул пустой файл.");
        return (total,
            Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private static void VerifyDigest(string actual, string? expected, string label)
    {
        if (expected is not null && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Загрузка не совпадает с {label}.");
    }

    public void Dispose() => _http.Dispose();
}
