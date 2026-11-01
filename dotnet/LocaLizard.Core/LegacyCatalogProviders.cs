using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocaLizard.Core;

/// <summary>
/// Reads the language-pack indexes published with W10MUI. These files are metadata
/// catalogs, not a binary store; every discovered URL is re-validated before use.
/// </summary>
public sealed class W10MuiCatalogProvider : ILanguageResourceProvider, IDisposable
{
    private const string RawRoot = "https://raw.githubusercontent.com/abbodi1406/BatUtil/master/W10MUI";
    private readonly HttpClient _http;

    public W10MuiCatalogProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public string SourceId => "w10mui";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        if (!PackageCompatibilityService.IsKnownBuildFamily(request.BuildFamily, request.ProductFamily))
            return [];
        var catalogBuild = CatalogBuild(request.BuildFamily);
        if (catalogBuild is null) return [];

        var directory = request.ResourceType switch
        {
            LanguageResourceType.FeatureOnDemand => "OnDemand",
            _ => "Langs"
        };
        var suffix = directory == "Langs" ? "-LPs.txt" : "-OnDemand.txt";
        var fileNames = directory == "Langs"
            ? new[] { $"{catalogBuild}{suffix}", $"{catalogBuild}{suffix.Replace(".txt", "_Alternative.txt")}" }
            : new[] { $"{catalogBuild}{suffix}", $"{catalogBuild}-OnDemand_Alternative.txt" };

        foreach (var fileName in fileNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = new Uri($"{RawRoot}/{directory}/{Uri.EscapeDataString(fileName)}");
            if (!SecurityPolicy.IsTrustedMetadataUrl(uri)) continue;
            string text;
            try
            {
                using var response = await _http.GetAsync(uri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is not { } finalUri ||
                    !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
                    throw new InvalidDataException("W10MUI metadata redirect left the allow-listed raw GitHub catalog path.");
                text = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException) { continue; }

            var parsed = ParseCatalog(text, request, catalogBuild.Value,
                string.Equals(directory, "OnDemand", StringComparison.OrdinalIgnoreCase));
            if (parsed.Count > 0) return parsed;
        }

        return [];
    }

    internal static int? CatalogBuild(int targetBuild) => targetBuild switch
    {
        10240 => 10240,
        14393 => 14393,
        17763 => 17763,
        19044 => 19041,
        26100 => 26100,
        _ => null
    };

    internal static IReadOnlyList<LanguageResourceCandidate> ParseCatalog(
        string text, LanguageResourceRequest request, int catalogBuild, bool featureOnDemand = false)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var results = new List<LanguageResourceCandidate>();
        var currentSection = "";
        var inRussianSection = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"(?i)^[a-z]{2,3}-[a-z]{2,4}$"))
            {
                inRussianSection = line.Equals("ru-RU", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                (line.Contains("x64", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("x86", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Client", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("Server", StringComparison.OrdinalIgnoreCase)))
            {
                currentSection = line;
            }
            if (!inRussianSection ||
                !(line.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  line.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (!Uri.TryCreate(line, UriKind.Absolute, out var parsedUri)) continue;
            var fileName = Path.GetFileName(parsedUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) continue;

            var isServer = PackageCompatibilityService.PackageNameLooksServer(fileName) ||
                           currentSection.Contains("Server", StringComparison.OrdinalIgnoreCase);
            if (isServer != PackageCompatibilityService.IsServer(request.ProductFamily)) continue;
            var type = featureOnDemand ? LanguageResourceType.FeatureOnDemand :
                isServer ? LanguageResourceType.ServerLanguagePack : LanguageResourceType.ClientLanguagePack;
            if (request.ResourceType is not null && request.ResourceType != type) continue;
            if (featureOnDemand)
            {
                if (!PackageCompatibilityService.IsFodPackage(fileName) ||
                    !fileName.Contains("ru-ru", StringComparison.OrdinalIgnoreCase) &&
                    !fileName.Contains("ru-RU", StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            var architecture = InferArchitecture(fileName);
            if (architecture is null || !string.Equals(architecture, request.Architecture, StringComparison.OrdinalIgnoreCase))
                continue;
            if (type == LanguageResourceType.ClientLanguagePack &&
                !WindowsImageService.IsClientLpName(fileName, architecture))
                continue;
            if (type == LanguageResourceType.ServerLanguagePack &&
                (!fileName.Contains("server-languagepack", StringComparison.OrdinalIgnoreCase) &&
                 !fileName.Contains("server-language-pack", StringComparison.OrdinalIgnoreCase)))
                continue;

            var downloadUri = ToTrustedHttps(parsedUri);
            if (downloadUri is null || !SecurityPolicy.IsTrustedPackageUrl(downloadUri)) continue;
            results.Add(new LanguageResourceCandidate(
                $"w10mui:{catalogBuild}:{fileName}", "w10mui", fileName, type, "ru-RU",
                architecture, request.BuildFamily, request.ProductFamily, fileName,
                DownloadUri: downloadUri, TrustLevel: ResourceTrustLevel.MicrosoftHostedCatalog,
                Notes: $"Metadata from W10MUI {catalogBuild}; file integrity/signature is verified after download."));
        }

        return results;
    }

    private static Uri? ToTrustedHttps(Uri uri)
    {
        if (!SecurityPolicy.IsTrustedPackageUrl(uri) &&
            !(uri.Scheme == Uri.UriSchemeHttp &&
              uri.Host.EndsWith(".mp.microsoft.com", StringComparison.OrdinalIgnoreCase)))
            return null;
        var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
        return builder.Uri;
    }

    private static string? InferArchitecture(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower.Contains("arm64")) return "arm64";
        if (lower.Contains("amd64") || lower.Contains("x64") || lower.Contains("x86_64")) return "x64";
        if (Regex.IsMatch(lower, @"(?:~|_|-)x86(?:~|_|-|\.)")) return "x86";
        return null;
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Parses the PowerShell CLIXML metadata catalogs shipped by OSBuilder.Public.
/// Only Russian language-pack/FOD rows with an explicit build and architecture are returned.
/// </summary>
public sealed class OsBuilderCatalogProvider : ILanguageResourceProvider, IDisposable
{
    private const string RawRoot = "https://raw.githubusercontent.com/OSDeploy/OSBuilder.Public/master/Content/Updates";
    private readonly HttpClient _http;

    public OsBuilderCatalogProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public string SourceId => "osbuilder-public";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        var catalogs = request.ResourceType switch
        {
            LanguageResourceType.FeatureOnDemand =>
                new[] { "FeatureOnDemand/CatFeatureOnDemand.xml", "Catalog 2018 FeatureOnDemand Windows 10 1709-1803.xml" },
            LanguageResourceType.LanguageInterfacePack =>
                new[] { "LanguageInterfacePack/CatLanguageInterfacePack.xml", "Catalog 2018 LanguageInterfacePack Windows 10 1607-1803.xml" },
            _ => new[] { "LanguagePack/CatLanguagePack.xml", "Catalog 2018 LanguagePack Windows 10 1607-1803.xml" }
        };

        foreach (var catalog in catalogs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string payload;
            try
            {
                var uri = new Uri($"{RawRoot}/{string.Join("/", catalog.Split('/').Select(Uri.EscapeDataString))}");
                if (!SecurityPolicy.IsTrustedMetadataUrl(uri)) continue;
                using var response = await _http.GetAsync(uri, cancellationToken);
                if (response.StatusCode == HttpStatusCode.NotFound) continue;
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is not { } finalUri ||
                    !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
                    throw new InvalidDataException("OSBuilder metadata redirect left the allow-listed raw GitHub catalog path.");
                payload = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException) { continue; }

            var results = ParseCatalog(payload, request);
            if (results.Count > 0) return results;
        }
        return [];
    }

    internal static IReadOnlyList<LanguageResourceCandidate> ParseCatalog(
        string payload, LanguageResourceRequest request)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = 32 * 1024 * 1024
        };
        using var stringReader = new StringReader(payload);
        using var xmlReader = XmlReader.Create(stringReader, settings);
        var document = XDocument.Load(xmlReader, LoadOptions.None);
        var results = new List<LanguageResourceCandidate>();

        foreach (var obj in document.Descendants().Where(x => x.Name.LocalName == "Obj"))
        {
            var values = obj.Descendants()
                .Where(x => x.Name.LocalName == "S")
                .Where(x => x.Attribute("N") is not null)
                .GroupBy(x => (string)x.Attribute("N")!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value.Trim(), StringComparer.OrdinalIgnoreCase);

            string Field(string name) => values.TryGetValue(name, out var value) ? value : "";
            var category = Field("Category");
            var title = Field("KBTitle");
            var fileName = Field("FileName");
            var rawUrl = Field("URL");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(rawUrl) ||
                !title.Contains("Russian", StringComparison.OrdinalIgnoreCase) &&
                !title.Contains("ru-RU", StringComparison.OrdinalIgnoreCase))
                continue;

            var build = ParseBuildFamily(title);
            var architecture = title.Contains("x64-based", StringComparison.OrdinalIgnoreCase) ? "x64" :
                title.Contains("x86-based", StringComparison.OrdinalIgnoreCase) ? "x86" :
                title.Contains("ARM64-based", StringComparison.OrdinalIgnoreCase) ? "arm64" : null;
            if (build != request.BuildFamily || architecture != request.Architecture) continue;
            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)) continue;
            var downloadUri = ToTrustedHttps(uri);
            if (downloadUri is null || !SecurityPolicy.IsTrustedPackageUrl(downloadUri)) continue;

            var type = category.ToLowerInvariant() switch
            {
                "languagepack" => PackageCompatibilityService.IsServer(title)
                    ? LanguageResourceType.ServerLanguagePack : LanguageResourceType.ClientLanguagePack,
                "languageinterfacepack" => LanguageResourceType.LanguageInterfacePack,
                "featureondemand" => LanguageResourceType.FeatureOnDemand,
                _ => (LanguageResourceType?)null
            };
            if (type is null || request.ResourceType is not null && request.ResourceType != type) continue;
            if (PackageCompatibilityService.IsServer(request.ProductFamily) !=
                (type == LanguageResourceType.ServerLanguagePack)) continue;
            if (type == LanguageResourceType.ClientLanguagePack &&
                !title.Contains("LanguagePack", StringComparison.OrdinalIgnoreCase)) continue;
            if (type == LanguageResourceType.FeatureOnDemand &&
                !PackageCompatibilityService.IsFodPackage(fileName + " " + title)) continue;

            if (string.IsNullOrWhiteSpace(fileName)) fileName = Path.GetFileName(downloadUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(fileName)) continue;
            results.Add(new LanguageResourceCandidate(
                $"osbuilder:{build}:{fileName}:{architecture}", "osbuilder-public", title, type.Value,
                "ru-RU", architecture, build, request.ProductFamily, fileName,
                DownloadUri: downloadUri, TrustLevel: ResourceTrustLevel.MicrosoftHostedCatalog,
                Notes: $"Historical OSBuilder CLIXML catalog row: {title}. The final package must pass Microsoft signature verification."));
        }

        return results;
    }

    private static int ParseBuildFamily(string title)
    {
        var match = Regex.Match(title, @"(?i)Windows\s+10\s+Version\s+(\d{4})");
        if (!match.Success) return 0;
        return match.Groups[1].Value switch
        {
            "1607" => 14393,
            "1703" => 15063,
            "1709" => 16299,
            "1803" => 17134,
            "1809" => 17763,
            _ => 0
        };
    }

    private static Uri? ToTrustedHttps(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host != "download.windowsupdate.com" &&
            host != "wsus.ds.download.windowsupdate.com" &&
            host != "catalog.s.download.windowsupdate.com")
            return null;
        return new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri;
    }

    public void Dispose() => _http.Dispose();
}
