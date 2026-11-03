using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaLizard.Core;

public sealed record ManualPackageImportResult(
    IReadOnlyList<string> ImportedFiles,
    IReadOnlyList<string> SkippedFiles,
    string? ImportedMediaIso,
    string? FodDirectory,
    string? LxpDirectory,
    string ManifestPath);

public sealed class ManualPackageImportService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cab", ".esd", ".iso", ".appx", ".msix", ".appxbundle", ".msixbundle", ".xml"
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<ManualPackageImportResult> ImportAsync(
        string sourceId,
        IReadOnlyList<string> sourceFiles,
        string packageRoot,
        string? expectedDigest,
        CancellationToken ct,
        Action<string>? log = null,
        string? sourceReferenceUrl = null)
    {
        if (sourceFiles.Count == 0) throw new InvalidOperationException("Выберите хотя бы один файл для импорта.");
        if (LanguageSourceCatalog.Find(sourceId) is not { } source ||
            sourceId.Equals("m417z-language-files", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выбранный источник является справочником извлечённых ресурсов и не принимает установочные файлы.");
        if (source.Kind != LanguageSourceKind.ManualImport &&
            sourceId is not ("rg-adguard-files" or "what-the-hack"))
            throw new InvalidDataException("Для выбранного источника не поддерживается импорт локальных файлов.");

        if (!string.IsNullOrWhiteSpace(expectedDigest))
        {
            expectedDigest = expectedDigest.Trim();
            if (sourceFiles.Count != 1 || !Regex.IsMatch(expectedDigest, @"(?i)^(?:[a-f0-9]{40}|[a-f0-9]{64})$"))
                throw new InvalidDataException("Ручной контрольный хеш должен быть SHA-1 (40 знаков) или SHA-256 (64 знака) и применяться к одному файлу.");
        }

        Uri? explicitSourceReference = null;
        if (!string.IsNullOrWhiteSpace(sourceReferenceUrl))
        {
            if (!Uri.TryCreate(sourceReferenceUrl.Trim(), UriKind.Absolute, out explicitSourceReference) ||
                (!SecurityPolicy.IsPermittedExternalDownloadUrl(sourceId, explicitSourceReference) &&
                 !SecurityPolicy.IsTrustedCommunityDirectUrl(sourceId, explicitSourceReference)))
                throw new InvalidDataException("Ссылка происхождения файла не разрешена политикой выбранного источника.");
        }

        var root = Path.GetFullPath(packageRoot);
        Directory.CreateDirectory(root);
        var manifestPath = Path.Combine(root, "manual-imports.json");
        var document = await LoadManifestAsync(manifestPath, ct);
        var imported = new List<string>();
        var skipped = new List<string>();
        string? mediaIso = null;
        string? fodDirectory = null;
        string? lxpDirectory = null;

        using var fydexx = sourceId.Equals("fydexx-rs5", StringComparison.OrdinalIgnoreCase)
            ? new FydexxRs5MetadataProvider() : null;
        using var rgAdguard = sourceId.Equals("rg-adguard-files", StringComparison.OrdinalIgnoreCase)
            ? new RgAdguardFileMetadataProvider() : null;

        foreach (var originalPath in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var input = Path.GetFullPath(originalPath);
            if (!File.Exists(input)) throw new FileNotFoundException("Импортируемый файл не найден.", input);
            var attributes = File.GetAttributes(input);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Импорт символической ссылки/точки повторной обработки запрещён: {input}");

            var name = Path.GetFileName(input);
            var extension = Path.GetExtension(name);
            if (!SupportedExtensions.Contains(extension))
                throw new InvalidDataException($"Формат {extension} не поддерживается. Скрипты, EXE и произвольные архивы импортировать нельзя.");
            if (extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("License.xml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Из XML разрешено импортировать только License.xml для Store LXP.");

            var info = new FileInfo(input);
            if (info.Length <= 0 || info.Length > (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)
                    ? 16L * 1024 * 1024 * 1024 : 4L * 1024 * 1024 * 1024))
                throw new InvalidDataException($"Недопустимый размер файла: {name}");

            string? catalogSha1 = null;
            string? catalogSha256 = null;
            Uri? referenceUri = explicitSourceReference ??
                (source.InformationUrl is { } sourceUrl ? new Uri(sourceUrl) : null);

            if (fydexx is not null)
            {
                if (!extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains("_ru-ru.", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("fydexx RS5 reference accepts only a Russian language-pack CAB filename.");
                var metadata = await fydexx.FindAsync(name, ct)
                    ?? throw new InvalidDataException("Имя CAB отсутствует в закреплённом каталоге fydexx RS5.");
                if (metadata.BuildFamily != 17763 || metadata.Architecture is null ||
                    metadata.ProductFamily is null || metadata.Sha1 is null)
                    throw new InvalidDataException("Каталог fydexx не подтвердил build/архитектуру/семейство пакета.");
                catalogSha1 = metadata.Sha1;
                referenceUri = metadata.ReferenceUri;
            }
            else if (rgAdguard is not null)
            {
                var metadata = await rgAdguard.FindAsync(name, ct);
                if (metadata is null || (metadata.Sha256 is null && metadata.Sha1 is null))
                    throw new InvalidDataException("RG-Adguard не вернул независимый SHA-256/SHA-1 для точного имени файла. Можно повторить импорт из community-источника с ручным хешем.");
                catalogSha1 = metadata.Sha1;
                catalogSha256 = metadata.Sha256;
                referenceUri = metadata.ReferenceUri;
                if (metadata.Size is long expectedSize && expectedSize != info.Length)
                    throw new InvalidDataException("Размер файла не совпадает с RG-Adguard metadata.");
            }

            var destinationDirectory = extension.ToLowerInvariant() switch
            {
                ".iso" => Path.Combine(root, "ManualImport", "Media"),
                ".appx" or ".msix" or ".appxbundle" or ".msixbundle" or ".xml" =>
                    Path.Combine(root, "ManualImport", "ImportedLXP", "ru-ru"),
                ".cab" when PackageCompatibilityService.IsFodPackage(name) =>
                    Path.Combine(root, "ManualImport", "ImportedFoD", "ru-ru"),
                _ => Path.Combine(root, "ManualImport", "LanguagePacks", "ru-ru")
            };
            Directory.CreateDirectory(destinationDirectory);
            SecurityPolicy.EnsureSafeFileName(destinationDirectory, name);
            var destination = Path.Combine(destinationDirectory, name);
            var temporary = destination + ".importing";

            var copyResult = await CopyAndHashAsync(input, temporary, ct);
            try
            {
                if (copyResult.Size != info.Length)
                    throw new InvalidDataException("Размер файла изменился во время импорта.");
                if (!string.IsNullOrWhiteSpace(catalogSha1) &&
                    !copyResult.Sha1.Equals(catalogSha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Импортируемый файл не совпадает с SHA-1 из каталога.");
                if (!string.IsNullOrWhiteSpace(catalogSha256) &&
                    !copyResult.Sha256.Equals(catalogSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Импортируемый файл не совпадает с SHA-256 из RG-Adguard.");
                if (!string.IsNullOrWhiteSpace(expectedDigest))
                {
                    var actual = expectedDigest.Length == 40 ? copyResult.Sha1 : copyResult.Sha256;
                    if (!actual.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Импортируемый файл не совпадает с введённым контрольным хешем.");
                }

                if (File.Exists(destination))
                {
                    var existing = await HashFileAsync(destination, ct);
                    if (!existing.Sha256.Equals(copyResult.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"В каталоге импорта уже есть файл с таким именем и другим содержимым: {name}");
                    File.Delete(temporary);
                    skipped.Add(destination);
                    log?.Invoke($"Файл уже импортирован без изменений: {name}");
                }
                else
                {
                    File.Move(temporary, destination);
                    imported.Add(destination);
                    log?.Invoke($"Импортирован {name}; SHA-256={copyResult.Sha256}");
                }

                if (extension.Equals(".iso", StringComparison.OrdinalIgnoreCase))
                    mediaIso ??= destination;
                if (extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) &&
                    PackageCompatibilityService.IsFodPackage(name))
                    fodDirectory = destinationDirectory;
                if (extension.Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    lxpDirectory = Path.Combine(root, "ManualImport", "ImportedLXP");
                        var relative = Path.GetRelativePath(root, destination).Replace('\\', '/');
                if (!document.Files.Any(x => x.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) &&
                                             x.Sha256.Equals(copyResult.Sha256, StringComparison.OrdinalIgnoreCase)))
                {
                    document.Files.Add(new ManualPackageImportManifestEntry(
                        sourceId, source.DisplayName, name, relative, copyResult.Size,
                        copyResult.Sha1, copyResult.Sha256, DateTimeOffset.UtcNow,
                        referenceUri?.AbsoluteUri));
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }

        await SaveManifestAsync(manifestPath, document, ct);
        return new ManualPackageImportResult(imported, skipped, mediaIso, fodDirectory, lxpDirectory, manifestPath);
    }

    public static bool IsSupportedImportExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return SupportedExtensions.Contains(extension) &&
               (!extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(fileName).Equals("License.xml", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(long Size, string Sha1, string Sha256)> CopyAndHashAsync(
        string inputPath, string temporaryPath, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        await output.FlushAsync(ct);
        return (total, Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<(long Size, string Sha1, string Sha256)> HashFileAsync(
        string path, CancellationToken ct)
    {
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            sha1.AppendData(buffer, 0, read);
            sha256.AppendData(buffer, 0, read);
        }
        return (total, Convert.ToHexString(sha1.GetHashAndReset()).ToLowerInvariant(),
            Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<ManualPackageImportManifest> LoadManifestAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new ManualPackageImportManifest();
        await using var stream = File.OpenRead(path);
        try
        {
            var document = await JsonSerializer.DeserializeAsync<ManualPackageImportManifest>(stream, JsonOptions, ct)
                ?? new ManualPackageImportManifest();
            if (document.SchemaVersion != 1 || document.Files is null)
                throw new InvalidDataException("manual-imports.json has an unsupported schema.");
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("manual-imports.json is invalid; it was not overwritten.", ex);
        }
    }

    private static async Task SaveManifestAsync(string path, ManualPackageImportManifest document, CancellationToken ct)
    {
        var temporary = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                             32 * 1024, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, ct);
            File.Move(temporary, path, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    public sealed class ManualPackageImportManifest
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; } = 1;
        [JsonPropertyName("files")] public List<ManualPackageImportManifestEntry> Files { get; set; } = [];
    }

    public sealed record ManualPackageImportManifestEntry(
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("source_name")] string SourceName,
        [property: JsonPropertyName("file_name")] string FileName,
        [property: JsonPropertyName("relative_path")] string RelativePath,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("imported_utc")] DateTimeOffset ImportedUtc,
        [property: JsonPropertyName("reference_url")] string? ReferenceUrl);
}
