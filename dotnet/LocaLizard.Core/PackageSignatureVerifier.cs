using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record PackageSignatureResult(
    bool IsValid,
    string Status,
    string? Subject = null,
    string? Thumbprint = null,
    string? Error = null);

public sealed class PackageSignatureVerifier(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<PackageSignatureResult> VerifyMicrosoftAsync(string packagePath, CancellationToken ct)
    {
        if (!File.Exists(packagePath)) return new(false, "missing", Error: "Package file not found.");
        if (!OperatingSystem.IsWindows()) return new(false, "unsupported", Error: "Authenticode verification requires Windows.");
        var escaped = Path.GetFullPath(packagePath).Replace("'", "''");
        var script = "$s=Get-AuthenticodeSignature -LiteralPath '" + escaped + "'; " +
                     "if ($s.Status -ne 'Valid' -or $null -eq $s.SignerCertificate -or " +
                     "$s.SignerCertificate.Subject -notmatch '(?i)Microsoft Corporation') { " +
                     "Write-Output ('SIGNATURE_INVALID|' + $s.Status + '|' + $s.SignerCertificate.Subject); exit 81 }; " +
                     "Write-Output ('SIGNATURE_VALID|' + $s.SignerCertificate.Subject + '|' + $s.SignerCertificate.Thumbprint)";
        try
        {
            var result = await _runner.RunAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(2));
            var line = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(x => x.StartsWith("SIGNATURE_", StringComparison.Ordinal));
            if (result.ExitCode != 0 || line is null || !line.StartsWith("SIGNATURE_VALID|", StringComparison.Ordinal))
                return new(false, "invalid", Error: line ?? result.StandardError);
            var fields = line.Split('|', 3);
            return new(true, "valid", fields.ElementAtOrDefault(1), fields.ElementAtOrDefault(2));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new(false, "error", Error: ex.Message);
        }
    }
}

public sealed class LegacyPackageDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    public async Task<LanguageResourceCandidate> DownloadVerifiedAsync(
        LanguageResourceCandidate candidate, LanguageResourceRequest request, string repositoryRoot,
        PackageSignatureVerifier signatureVerifier, ManagedRepositoryService manifest,
        ProcessRunner runner, CancellationToken ct, Action<string>? log = null)
    {
        if (candidate.DownloadUri is null || !SecurityPolicy.IsTrustedPackageUrl(candidate.DownloadUri) ||
            candidate.DownloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Legacy catalog URL is not an allow-listed HTTPS Microsoft CDN URL.");
        if (!candidate.FileName.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) &&
            !candidate.FileName.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only CAB and ESD language packages are supported by this downloader.");

        Directory.CreateDirectory(repositoryRoot);
        var effectiveFileName = Path.GetFileName(candidate.DownloadUri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(effectiveFileName)) effectiveFileName = candidate.FileName;
        SecurityPolicy.EnsureSafeFileName(repositoryRoot, effectiveFileName);
        var destination = Path.Combine(repositoryRoot, effectiveFileName);
        if (File.Exists(destination))
        {
            var existingSignature = await signatureVerifier.VerifyMicrosoftAsync(destination, ct);
            if (existingSignature.IsValid)
            {
                var inspector = new DismPackageIdentityInspector(runner);
                var normalized = await inspector.NormalizeFileNameAsync(destination, request, ct);
                var existingCandidate = candidate with
                {
                    FileName = Path.GetFileName(normalized.Equals(destination, StringComparison.OrdinalIgnoreCase)
                        ? destination : Path.GetDirectoryName(normalized)!),
                    LocalPath = normalized,
                    TrustLevel = ResourceTrustLevel.MicrosoftSigned
                };
                await manifest.RegisterAsync(repositoryRoot, normalized, existingCandidate, existingSignature, ct);
                return existingCandidate;
            }
        }

        var temporary = destination + ".download";
        try
        {
            var current = candidate.DownloadUri;
            HttpResponseMessage? response = null;
            for (var redirect = 0; redirect <= 6; redirect++)
            {
                if (current.Scheme != Uri.UriSchemeHttps || !SecurityPolicy.IsTrustedPackageUrl(current))
                    throw new InvalidDataException($"Blocked legacy catalog redirect: {current}");
                using var httpRequest = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _http.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    var location = response.Headers.Location ??
                        throw new InvalidDataException("Legacy catalog redirect has no Location header.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    response = null;
                    continue;
                }
                response.EnsureSuccessStatusCode();
                break;
            }

            if (response is null || !SecurityPolicy.IsTrustedPackageUrl(current) ||
                current.Scheme != Uri.UriSchemeHttps)
                throw new InvalidDataException("Too many or untrusted legacy catalog redirects.");

            using (response)
            {
                var size = response.Content.Headers.ContentLength;
                if (size is null or <= 0 || size > 4L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Legacy package has no trustworthy bounded Content-Length.");
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > size || total > 4L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Legacy package exceeded the advertised download size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
                if (total != size) throw new InvalidDataException("Legacy package size differs from Content-Length.");
            }

            if (!string.IsNullOrWhiteSpace(candidate.Sha1))
            {
                await using var sha1Stream = File.OpenRead(temporary);
                var actualSha1 = Convert.ToHexString(await SHA1.HashDataAsync(sha1Stream, ct));
                if (!actualSha1.Equals(candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Downloaded package SHA-1 differs from the catalog metadata.");
            }

            var signature = await signatureVerifier.VerifyMicrosoftAsync(temporary, ct);
            if (!signature.IsValid)
                throw new InvalidDataException("Legacy package did not pass Microsoft Authenticode verification: " + signature.Error);

            File.Move(temporary, destination, true);
            var inspectorAfterDownload = new DismPackageIdentityInspector(runner);
            string normalizedPath;
            if (destination.EndsWith(".esd", StringComparison.OrdinalIgnoreCase))
            {
                var extractionRoot = Path.Combine(repositoryRoot, "legacy-esd-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(extractionRoot);
                try
                {
                    await runner.RunAsync("dism.exe",
                        ["/Apply-Image", $"/ImageFile:{destination}", "/Index:1", $"/ApplyDir:{extractionRoot}", "/CheckIntegrity"],
                        cancellationToken: ct, timeout: TimeSpan.FromMinutes(40));
                    var mum = AdkBootstrapper.EnumerateFilesSafely(extractionRoot, "update.mum")
                        .FirstOrDefault(x => x.Contains("WinSxS", StringComparison.OrdinalIgnoreCase));
                    if (mum is null)
                        throw new InvalidDataException("ESD passed integrity checks but contains no language-pack update.mum.");
                    normalizedPath = await inspectorAfterDownload.NormalizeFileNameAsync(mum, request, ct, repositoryRoot);
                }
                finally { try { Directory.Delete(extractionRoot, true); } catch { } }
            }
            else
            {
                normalizedPath = await inspectorAfterDownload.NormalizeFileNameAsync(destination, request, ct);
            }

            var normalizedCandidate = candidate with
            {
                FileName = Path.GetFileName(normalizedPath.Equals(destination, StringComparison.OrdinalIgnoreCase)
                    ? destination : Path.GetDirectoryName(normalizedPath)!),
                LocalPath = normalizedPath,
                Size = new FileInfo(normalizedPath).Length,
                TrustLevel = ResourceTrustLevel.MicrosoftSigned,
                Notes = $"Microsoft signature: {signature.Subject}; thumbprint: {signature.Thumbprint}"
            };
            await manifest.RegisterAsync(repositoryRoot, normalizedPath, normalizedCandidate, signature, ct);
            log?.Invoke($"Проверенный legacy-пакет Microsoft сохранён: {normalizedPath}");
            return normalizedCandidate;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}

public sealed class ManagedRepositoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public async Task RegisterAsync(
        string repositoryRoot, string filePath, LanguageResourceCandidate candidate,
        PackageSignatureResult? signature, CancellationToken ct)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("Package not found.", filePath);
        var root = Path.GetFullPath(repositoryRoot);
        var fullPath = Path.GetFullPath(filePath);
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Package path is outside the repository root.");

        var info = new FileInfo(fullPath);
        await using var stream = File.OpenRead(fullPath);
        var sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        await using var shaStream = File.OpenRead(fullPath);
        var sha1 = Convert.ToHexString(await SHA1.HashDataAsync(shaStream, ct)).ToLowerInvariant();

        var manifestPath = Path.Combine(root, "resource_manifest.json");
        Directory.CreateDirectory(root);
        RepositoryManifest manifest;
        try
        {
            manifest = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<RepositoryManifest>(await File.ReadAllTextAsync(manifestPath, ct), JsonOptions)
                    ?? new RepositoryManifest()
                : new RepositoryManifest();
        }
        catch (JsonException) { manifest = new RepositoryManifest(); }

        manifest.SchemaVersion = 1;
        manifest.Packages[relative] = new RepositoryPackageRecord(
            info.Length, sha1, sha256, candidate.SourceId, candidate.FileName,
            candidate.ResourceType.ToString(), "ru-RU", candidate.Architecture,
            candidate.BuildFamily, candidate.ProductFamily, signature?.Status ?? "not_checked",
            signature?.Subject, signature?.Thumbprint, DateTimeOffset.UtcNow);
        var temp = manifestPath + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, JsonOptions), ct);
        File.Move(temp, manifestPath, true);
    }

    public async Task<bool> VerifyAsync(string repositoryRoot, string filePath, CancellationToken ct)
    {
        try
        {
            var root = Path.GetFullPath(repositoryRoot);
            var fullPath = Path.GetFullPath(filePath);
            var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
            if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
            var manifestPath = Path.Combine(root, "resource_manifest.json");
            if (!File.Exists(manifestPath) || !File.Exists(fullPath)) return false;
            var manifest = JsonSerializer.Deserialize<RepositoryManifest>(
                await File.ReadAllTextAsync(manifestPath, ct), JsonOptions);
            if (manifest is null || manifest.SchemaVersion != 1 ||
                !manifest.Packages.TryGetValue(relative, out var record) ||
                !string.Equals(record.Language, "ru-RU", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.Architecture) || record.BuildFamily is null ||
                record.SignatureStatus != "valid")
                return false;
            var info = new FileInfo(fullPath);
            if (info.Length != record.Size || info.Length <= 0) return false;
            await using var stream = File.OpenRead(fullPath);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            return string.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    private sealed class RepositoryManifest
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;
        [JsonPropertyName("packages")]
        public Dictionary<string, RepositoryPackageRecord> Packages { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record RepositoryPackageRecord(
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("sha1")] string Sha1,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("source_id")] string SourceId,
        [property: JsonPropertyName("source_package")] string SourcePackage,
        [property: JsonPropertyName("resource_type")] string ResourceType,
        [property: JsonPropertyName("language")] string Language,
        [property: JsonPropertyName("architecture")] string Architecture,
        [property: JsonPropertyName("build_family")] int? BuildFamily,
        [property: JsonPropertyName("product_family")] string ProductFamily,
        [property: JsonPropertyName("signature_status")] string SignatureStatus,
        [property: JsonPropertyName("signer_subject")] string? SignerSubject,
        [property: JsonPropertyName("signer_thumbprint")] string? SignerThumbprint,
        [property: JsonPropertyName("registered_at")] DateTimeOffset RegisteredAt);
}
