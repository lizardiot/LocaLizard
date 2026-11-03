using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

/// <summary>
/// Reads a pinned Dexit Windows 10 RS1 list. It is metadata only: the package
/// must still come from Microsoft's CDN and pass Authenticode/CBS verification.
/// </summary>
public sealed class DexitGistProvider : ILanguageResourceProvider, IDisposable
{
    public const string CatalogUrl =
        "https://api.github.com/gists/87f30cffa70ea6899177f536a8dc788f/c9ec1ecb985a62b7610263254fb2e2ccb2c968c1";

    private readonly HttpClient _http;

    public DexitGistProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public string SourceId => "dexit-gist";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        if (request.BuildFamily != 14393 ||
            !request.ProductFamily.Equals("Client", StringComparison.OrdinalIgnoreCase) ||
            !(request.Architecture.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
              request.Architecture.Equals("x64", StringComparison.OrdinalIgnoreCase)) ||
            request.ResourceType is not null && request.ResourceType != LanguageResourceType.ClientLanguagePack)
            return [];

        var uri = new Uri(CatalogUrl);
        if (!SecurityPolicy.IsTrustedMetadataUrl(uri)) return [];
        using var response = await _http.GetAsync(uri, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return [];
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
            throw new InvalidDataException("Dexit Gist metadata redirect left the pinned GitHub API endpoint.");

        if (response.Content.Headers.ContentLength is > 2 * 1024 * 1024)
            throw new InvalidDataException("Dexit Gist metadata exceeds the 2 MiB limit.");
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (payload.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Dexit Gist metadata exceeds the 2 MiB limit.");

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("files", out var files) ||
            !files.TryGetProperty("LanguagePacks.txt", out var file) ||
            !file.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Pinned Dexit Gist does not contain LanguagePacks.txt.");

        return ParseCatalog(content.GetString() ?? "", request);
    }

    internal static IReadOnlyList<LanguageResourceCandidate> ParseCatalog(
        string payload, LanguageResourceRequest request)
    {
        if (payload.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Dexit catalog exceeds the 2 MiB limit.");
        if (request.BuildFamily != 14393 ||
            !request.ProductFamily.Equals("Client", StringComparison.OrdinalIgnoreCase) ||
            request.ResourceType is not null && request.ResourceType != LanguageResourceType.ClientLanguagePack)
            return [];

        var build = Regex.Match(payload, @"(?i)Windows\s+10\s+build\s+10\.0\.(\d+)");
        if (!build.Success || !int.TryParse(build.Groups[1].Value, out var catalogBuild) ||
            catalogBuild != request.BuildFamily)
            return [];

        var results = new List<LanguageResourceCandidate>();
        string? currentArchitecture = null;
        foreach (var rawLine in payload.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            var architecture = Regex.Match(line, @"(?i)^\|\s*(x86|x64)\s*$");
            if (architecture.Success)
            {
                currentArchitecture = architecture.Groups[1].Value.ToLowerInvariant();
                continue;
            }

            var russian = Regex.Match(line, @"(?i)^\|\s*ru-RU\s+(https?://\S+\.cab)\s*$");
            if (!russian.Success || currentArchitecture is null ||
                !currentArchitecture.Equals(request.Architecture, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!Uri.TryCreate(russian.Groups[1].Value, UriKind.Absolute, out var sourceUri) ||
                sourceUri.Scheme is not ("http" or "https"))
                continue;
            var downloadUri = new UriBuilder(sourceUri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
            if (!SecurityPolicy.IsTrustedPackageUrl(downloadUri)) continue;

            var fileName = Path.GetFileName(downloadUri.AbsolutePath);
            var hash = Regex.Match(fileName, @"(?i)^lp_([a-f0-9]{40})\.cab$");
            if (!hash.Success) continue;
            var sha1 = hash.Groups[1].Value.ToLowerInvariant();
            results.Add(new LanguageResourceCandidate(
                $"dexit:14393:{currentArchitecture}:{sha1}",
                "dexit-gist",
                $"Dexit Windows 10 RS1 Russian LP ({currentArchitecture})",
                LanguageResourceType.ClientLanguagePack,
                "ru-RU",
                currentArchitecture,
                14393,
                "Client",
                fileName,
                DownloadUri: downloadUri,
                Sha1: sha1,
                TrustLevel: ResourceTrustLevel.ThirdPartyMetadataMicrosoftFile,
                Notes: "Pinned Dexit metadata only. Microsoft CDN download, SHA-1, Authenticode and CBS Package Identity are mandatory."));
        }

        return results.GroupBy(x => x.DownloadUri!.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First()).ToArray();
    }

    public void Dispose() => _http.Dispose();
}
