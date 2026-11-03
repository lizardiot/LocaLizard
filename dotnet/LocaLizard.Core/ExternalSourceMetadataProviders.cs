using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record ExternalFileMetadata(
    string SourceId,
    string FileName,
    string? Sha1,
    string? Sha256,
    Uri? ReferenceUri = null,
    long? Size = null,
    int? BuildFamily = null,
    string? Architecture = null,
    string? ProductFamily = null);

public sealed class FydexxRs5MetadataProvider : IDisposable
{
    public const string CatalogUrl =
        "https://gist.githubusercontent.com/fydexx/020e34f98df023d5970f353a443fe396/raw/b30ee1d71b1f680d6b24de53a9aa745157c877a9/language_packs.txt";

    private readonly HttpClient _http;

    public FydexxRs5MetadataProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public async Task<ExternalFileMetadata?> FindAsync(string fileName, CancellationToken ct)
    {
        if (!SecurityPolicy.IsTrustedMetadataUrl(new Uri(CatalogUrl)))
            throw new InvalidDataException("Pinned fydexx metadata URL is not allow-listed.");
        using var response = await _http.GetAsync(CatalogUrl, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
            throw new InvalidDataException("fydexx metadata redirect left its pinned Gist revision.");
        var payload = await ReadBoundedTextAsync(response, 4 * 1024 * 1024, ct);
        return ParseCatalog(payload).FirstOrDefault(x =>
            x.FileName.Equals(Path.GetFileName(fileName), StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<ExternalFileMetadata> ParseCatalog(string payload)
    {
        if (payload.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("fydexx catalog exceeds the 4 MiB limit.");

        var results = new List<ExternalFileMetadata>();
        var row = new Regex(
            @"(?im)^\s*\|\s*(?<arch>x86|x64|arm64)\\langpacks\\(?<file>Microsoft-Windows-(?<family>Client|Server)-Language-Pack_[^\\]+\.cab)\s+SHA-1:\s*(?<sha1>[a-f0-9]{40})\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        foreach (Match match in row.Matches(payload))
        {
            var fileName = match.Groups["file"].Value.Trim();
            var architecture = BuildNumbers.Architecture(match.Groups["arch"].Value);
            var productFamily = match.Groups["family"].Value.Equals("Server", StringComparison.OrdinalIgnoreCase)
                ? "Server" : "Client";
            if (!fileName.Contains("_ru-ru.", StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(new ExternalFileMetadata(
                "fydexx-rs5",
                fileName,
                match.Groups["sha1"].Value.ToLowerInvariant(),
                null,
                new Uri(CatalogUrl),
                BuildFamily: 17763,
                Architecture: architecture,
                ProductFamily: productFamily));
        }

        return results
            .GroupBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToArray();
    }

    public void Dispose() => _http.Dispose();

    internal static async Task<string> ReadBoundedTextAsync(
        HttpResponseMessage response, int maximumBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is long length && (length < 0 || length > maximumBytes))
            throw new InvalidDataException($"Metadata response exceeds the {maximumBytes:N0}-byte limit.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"Metadata response exceeds the {maximumBytes:N0}-byte limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }
}

/// <summary>
/// Uses RG-Adguard only as an independent hash reference. It never downloads
/// a language package from the RG-Adguard site or follows a third-party mirror.
/// </summary>
public sealed class RgAdguardFileMetadataProvider : IDisposable
{
    public const string SearchUrl = "https://files.rg-adguard.net/search";
    private readonly HttpClient _http;

    public RgAdguardFileMetadataProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public async Task<ExternalFileMetadata?> FindAsync(string fileName, CancellationToken ct)
    {
        fileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 240 ||
            !Regex.IsMatch(fileName, @"^[A-Za-z0-9_.~+-]+$"))
            throw new InvalidDataException("RG-Adguard lookup requires a plain package filename.");

        var searchUri = new Uri(SearchUrl);
        if (!SecurityPolicy.IsTrustedMetadataUrl(searchUri))
            throw new InvalidDataException("RG-Adguard search endpoint is not allow-listed.");

        var searchHtml = await SendBoundedAsync(
            HttpMethod.Post, searchUri,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["search"] = fileName }),
            ct);

        foreach (var detailsUri in ParseDetailsLinks(searchHtml).Take(5))
        {
            var detailsHtml = await SendBoundedAsync(HttpMethod.Get, detailsUri, null, ct);
            var metadata = ParseDetailsPage(detailsHtml, fileName, detailsUri);
            if (metadata is not null) return metadata;
        }

        return null;
    }

    internal static IReadOnlyList<Uri> ParseDetailsLinks(string html)
    {
        if (html.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("RG-Adguard search page exceeds the 2 MiB limit.");

        var links = new List<Uri>();
        foreach (Match match in Regex.Matches(html,
                     @"(?i)href\s*=\s*['""](?<url>(?:https://files\.rg-adguard\.net)?/file/[a-f0-9-]{8,64}/?)['""]"))
        {
            var value = match.Groups["url"].Value;
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
                uri = absolute;
            else
                uri = new Uri(new Uri("https://files.rg-adguard.net"), value);
            if (SecurityPolicy.IsTrustedMetadataUrl(uri) &&
                !links.Any(x => x.AbsoluteUri.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                links.Add(uri);
        }
        return links;
    }

    internal static ExternalFileMetadata? ParseDetailsPage(string html, string expectedFileName, Uri detailsUri)
    {
        if (html.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("RG-Adguard details page exceeds the 2 MiB limit.");
        if (!SecurityPolicy.IsTrustedMetadataUrl(detailsUri) ||
            !html.Contains(expectedFileName, StringComparison.OrdinalIgnoreCase))
            return null;

        var sha256 = Regex.Match(html, @"(?i)SHA[- ]?256\s*[:：]\s*(?<hash>[a-f0-9]{64})");
        var sha1 = Regex.Match(html, @"(?i)SHA[- ]?1\s*[:：]\s*(?<hash>[a-f0-9]{40})");
        var size = Regex.Match(html, @"(?i)(?:File\s+)?Size\s*[:：]\s*(?<size>\d{1,15})\s*(?:bytes?)?\b");
        var sha256Value = sha256.Success ? sha256.Groups["hash"].Value.ToLowerInvariant() : null;
        var sha1Value = sha1.Success ? sha1.Groups["hash"].Value.ToLowerInvariant() : null;
        if (sha256Value is null && sha1Value is null) return null;

        long? sizeValue = size.Success && long.TryParse(size.Groups["size"].Value, out var parsed)
            ? parsed : null;
        return new ExternalFileMetadata(
            "rg-adguard-files", expectedFileName, sha1Value, sha256Value,
            detailsUri, sizeValue);
    }

    private async Task<string> SendBoundedAsync(
        HttpMethod method, Uri uri, HttpContent? content, CancellationToken ct)
    {
        var current = uri;
        var currentMethod = method;
        var currentContent = content;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            if (!SecurityPolicy.IsTrustedMetadataUrl(current))
                throw new InvalidDataException("RG-Adguard metadata request left the exact allow-listed host/path.");
            using var request = new HttpRequestMessage(currentMethod, current)
            {
                Content = currentContent
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidDataException("RG-Adguard returned a redirect without Location.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!SecurityPolicy.IsTrustedMetadataUrl(current))
                    throw new InvalidDataException("RG-Adguard redirect target is not allow-listed.");
                currentMethod = HttpMethod.Get;
                currentContent = null;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { } finalUri ||
                !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
                throw new InvalidDataException("RG-Adguard final URL is not allow-listed.");
            return await FydexxRs5MetadataProvider.ReadBoundedTextAsync(response, 2 * 1024 * 1024, ct);
        }
        throw new InvalidDataException("RG-Adguard exceeded the metadata redirect limit.");
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Extracts two historical Microsoft ISO links from a pinned WhatTheHack script.
/// The PowerShell script itself is never downloaded to disk, executed or trusted.
/// </summary>
public sealed class WhatTheHackMediaReferenceProvider : IDisposable
{
    public const string ScriptUrl =
        "https://raw.githubusercontent.com/microsoft/WhatTheHack/53868d7819be6c83a507ecd6c2971a4b1d4de286/037-AzureVirtualDesktop/Coach/Solutions/04-installLanguagePacks.ps1";
    private readonly HttpClient _http;

    public WhatTheHackMediaReferenceProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public async Task<IReadOnlyList<MicrosoftMediaReference>> DiscoverAsync(CancellationToken ct)
    {
        var uri = new Uri(ScriptUrl);
        if (!SecurityPolicy.IsTrustedMetadataUrl(uri))
            throw new InvalidDataException("WhatTheHack script URL is not pinned/allow-listed.");
        using var response = await _http.GetAsync(uri, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
            throw new InvalidDataException("WhatTheHack metadata redirect left its pinned raw source.");
        var script = await FydexxRs5MetadataProvider.ReadBoundedTextAsync(response, 128 * 1024, ct);
        return ParseScript(script);
    }

    internal static IReadOnlyList<MicrosoftMediaReference> ParseScript(string script)
    {
        if (script.Length > 128 * 1024)
            throw new InvalidDataException("WhatTheHack reference script exceeds the 128 KiB limit.");

        var found = new List<MicrosoftMediaReference>();
        foreach (var rawLine in script.Replace("\r", "").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var match = Regex.Match(line,
                @"(?i)^\$(?<key>langIsoUrl|fodIsoUrl)\s*=\s*['""](?<url>https://[^'""]+)['""]\s*$");
            if (!match.Success) continue;
            if (!Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var downloadUri) ||
                !SecurityPolicy.IsTrustedMicrosoftMediaUrl(downloadUri))
                continue;

            var isLanguage = match.Groups["key"].Value.Equals("langIsoUrl", StringComparison.OrdinalIgnoreCase);
            var name = Path.GetFileName(downloadUri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name) ||
                !downloadUri.AbsolutePath.Contains("/19041.1.", StringComparison.OrdinalIgnoreCase))
                continue;

            found.Add(new MicrosoftMediaReference(
                isLanguage ? "what-the-hack-language-iso" : "what-the-hack-fod-iso",
                isLanguage ? "Windows 10 20H2 Language Pack ISO" : "Windows 10 20H2 Features on Demand ISO",
                "Windows 10 20H2 · base media build 19041.1 · x64",
                downloadUri,
                name,
                "Historical reference only. Open the Microsoft download URL manually; import and inspect the ISO in LocaLizard before use."));
        }

        return found.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToArray();
    }

    public void Dispose() => _http.Dispose();
}

public sealed record MicrosoftMediaReference(
    string Id,
    string DisplayName,
    string BuildLabel,
    Uri DownloadUri,
    string FileName,
    string Notes);
