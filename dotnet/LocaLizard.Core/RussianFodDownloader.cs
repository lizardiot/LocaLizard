using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record FodPackageIdentity(
    string Identity, string Capability, string Architecture, int BuildFamily, string Language);

public sealed class FodPackageIdentityInspector(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<FodPackageIdentity> InspectAsync(string packagePath, LanguageResourceRequest request, CancellationToken ct)
    {
        var result = await _runner.RunAsync("dism.exe",
            ["/Get-PackageInfo", $"/PackagePath:{Path.GetFullPath(packagePath)}", "/English"],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(10));
        var match = Regex.Match(result.StandardOutput, @"(?im)^\s*Package Identity\s*:\s*(.+?)\s*$");
        if (!match.Success) throw new InvalidDataException("DISM не вернул Package Identity для FOD CAB.");
        var identity = match.Groups[1].Value.Trim();
        var lower = identity.ToLowerInvariant();
        if (!lower.Contains("ru-ru", StringComparison.Ordinal) ||
            !(lower.Contains("languagefeatures", StringComparison.Ordinal) ||
              lower.Contains("language.fonts", StringComparison.Ordinal) ||
              lower.Contains("wino cr", StringComparison.Ordinal)))
            throw new InvalidDataException("Пакет не является языковым FOD ru-RU.");
        var architecture = lower.Contains("arm64") ? "arm64" :
            lower.Contains("amd64") || lower.Contains("x64") ? "x64" :
            Regex.IsMatch(lower, @"(?:~|_|-)x86(?:~|_|-|$)") ? "x86" : "";
        if (!architecture.Equals(request.Architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"FOD CAB architecture {architecture} does not match {request.Architecture}.");
        var buildMatch = Regex.Match(identity, @"(?<!\d)10\.0\.(\d{5})\.");
        var build = buildMatch.Success && int.TryParse(buildMatch.Groups[1].Value, out var parsed)
            ? parsed : request.BuildFamily;
        if (build != request.BuildFamily)
            throw new InvalidDataException($"FOD CAB build {build} does not match target {request.BuildFamily}.");
        var capability = CapabilityName(identity);
        return new FodPackageIdentity(identity, capability, architecture, build, "ru-RU");
    }

    private static string CapabilityName(string identity)
    {
        var lower = identity.ToLowerInvariant();
        if (lower.Contains("languagefeatures-basic")) return "Basic";
        if (lower.Contains("languagefeatures-ocr") || lower.Contains("wino cr")) return "OCR";
        if (lower.Contains("languagefeatures-handwriting")) return "Handwriting";
        if (lower.Contains("languagefeatures-speech")) return "Speech";
        if (lower.Contains("languagefeatures-texttospeech")) return "TextToSpeech";
        if (lower.Contains("languagefeatures-fonts") || lower.Contains("language.fonts")) return "Fonts";
        return "Unknown";
    }
}

public sealed class RussianFodPackageDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(15)
    };

    public async Task<IReadOnlyList<string>> DownloadAsync(
        IEnumerable<LanguageResourceCandidate> candidates, LanguageResourceRequest request,
        string repositoryRoot, PackageSignatureVerifier signatures, ManagedRepositoryService manifest,
        ProcessRunner runner, CancellationToken ct, Action<string>? log = null)
    {
        Directory.CreateDirectory(repositoryRoot);
        var downloaded = new List<string>();
        var inspector = new FodPackageIdentityInspector(runner);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (candidate.ResourceType != LanguageResourceType.FeatureOnDemand ||
                candidate.DownloadUri is null || candidate.DownloadUri.Scheme != Uri.UriSchemeHttps ||
                !SecurityPolicy.IsTrustedPackageUrl(candidate.DownloadUri))
                continue;
            var urlName = Path.GetFileName(candidate.DownloadUri.AbsolutePath);
            if (!urlName.EndsWith(".cab", StringComparison.OrdinalIgnoreCase)) continue;
            SecurityPolicy.EnsureSafeFileName(repositoryRoot, urlName);
            var destination = Path.Combine(repositoryRoot, urlName);
            if (!File.Exists(destination))
            {
                var temporary = destination + ".download";
                try
                {
                    var current = candidate.DownloadUri;
                    HttpResponseMessage? response = null;
                    for (var redirects = 0; redirects <= 6; redirects++)
                    {
                        if (current.Scheme != Uri.UriSchemeHttps || !SecurityPolicy.IsTrustedPackageUrl(current))
                            throw new InvalidDataException($"Blocked FOD download redirect: {current}");
                        using var requestMessage = new HttpRequestMessage(HttpMethod.Get, current);
                        response = await _http.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, ct);
                        if ((int)response.StatusCode is >= 300 and < 400)
                        {
                            var location = response.Headers.Location ??
                                throw new InvalidDataException("FOD redirect has no Location.");
                            current = location.IsAbsoluteUri ? location : new Uri(current, location);
                            response.Dispose();
                            response = null;
                            continue;
                        }
                        response.EnsureSuccessStatusCode();
                        if (response.RequestMessage?.RequestUri is not { } finalUri ||
                            finalUri.Scheme != Uri.UriSchemeHttps || !SecurityPolicy.IsTrustedPackageUrl(finalUri))
                            throw new InvalidDataException("FOD download ended outside the allow-listed Microsoft HTTPS CDN.");
                        break;
                    }
                    if (response is null) throw new InvalidDataException("Too many FOD redirects.");
                    using (response)
                    {
                        var size = response.Content.Headers.ContentLength;
                        if (size is null or <= 0 || size > 1024L * 1024 * 1024)
                            throw new InvalidDataException("FOD CAB Content-Length is missing or exceeds 1 GiB.");
                        await using var input = await response.Content.ReadAsStreamAsync(ct);
                        await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var buffer = new byte[1024 * 1024];
                        long total = 0;
                        int read;
                        while ((read = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            total += read;
                            if (total > size || total > 1024L * 1024 * 1024)
                                throw new InvalidDataException("FOD download exceeded its advertised size.");
                            await output.WriteAsync(buffer.AsMemory(0, read), ct);
                        }
                        await output.FlushAsync(ct);
                        if (total != size) throw new InvalidDataException("FOD CAB size mismatch.");
                    }
                    var signature = await signatures.VerifyMicrosoftAsync(temporary, ct);
                    if (!signature.IsValid)
                        throw new InvalidDataException("FOD CAB has no valid Microsoft signature: " + signature.Error);
                    var identity = await inspector.InspectAsync(temporary, request, ct);
                    var record = candidate with
                    {
                        FileName = urlName,
                        LocalPath = destination,
                        TrustLevel = ResourceTrustLevel.MicrosoftSigned,
                        Notes = $"CBS identity: {identity.Identity}; capability: {identity.Capability}; signer: {signature.Subject}"
                    };
                    File.Move(temporary, destination, true);
                    await manifest.RegisterAsync(repositoryRoot, destination, record, signature, ct);
                    log?.Invoke($"Загружен и проверен ru-RU FOD {identity.Capability}: {urlName}");
                }
                catch
                {
                    try { File.Delete(temporary); } catch { }
                    throw;
                }
            }

            try
            {
                var signature = await signatures.VerifyMicrosoftAsync(destination, ct);
                if (!signature.IsValid) throw new InvalidDataException(signature.Error ?? "FOD signature invalid.");
                _ = await inspector.InspectAsync(destination, request, ct);
                downloaded.Add(destination);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log?.Invoke($"FOD CAB отклонён: {urlName}: {ex.Message}");
                try { File.Delete(destination); } catch { }
            }
        }
        return downloaded.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
