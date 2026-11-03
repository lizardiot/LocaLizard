using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record PixeldrainFileReference(
    string Id,
    string FileName,
    long Size,
    string Sha256,
    Uri DownloadUri);

/// <summary>
/// Resolves public Pixeldrain share links through the documented public file-info API.
/// It refuses files which require CAPTCHA, hit transfer limits, or are otherwise unavailable.
/// </summary>
public sealed class PixeldrainFileReferenceProvider : IDisposable
{
    private static readonly Uri BaseUri = new("https://pixeldrain.com/");
    private readonly HttpClient _http;

    public PixeldrainFileReferenceProvider(HttpClient? httpClient = null)
    {
        _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"LocaLizard/{AppInfo.Version}");
    }

    public async Task<PixeldrainFileReference> ResolveAsync(Uri shareUri, CancellationToken ct)
    {
        if (!TryGetFileId(shareUri, out var id))
            throw new InvalidDataException("Укажите публичную ссылку Pixeldrain вида https://pixeldrain.com/u/FILE_ID.");

        var infoUri = new Uri(BaseUri, $"api/file/{Uri.EscapeDataString(id)}/info");
        if (!SecurityPolicy.IsTrustedMetadataUrl(infoUri))
            throw new InvalidDataException("URL Pixeldrain metadata не прошёл проверку политики безопасности.");

        using var request = new HttpRequestMessage(HttpMethod.Get, infoUri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var json = await ReadBoundedAsync(response.Content, 256 * 1024, ct);
        return ParseFileInfo(json, id);
    }

    public static bool TryGetFileId(Uri? uri, out string id)
    {
        id = "";
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            uri.Port != 443 || !uri.Host.Equals("pixeldrain.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        var match = Regex.Match(uri.AbsolutePath,
            @"(?i)^/(?:u/|api/file/)(?<id>[a-z0-9_-]{6,32})/?$");
        if (!match.Success) return false;
        id = match.Groups["id"].Value;
        return true;
    }

    public static PixeldrainFileReference ParseFileInfo(string json, string expectedId)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("success", out var success) ||
            success.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("Pixeldrain не подтвердил существование публичного файла.");

        var id = GetString(root, "id");
        var name = GetString(root, "name");
        var size = root.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var parsedSize)
            ? parsedSize : 0;
        var sha256 = GetString(root, "hash_sha256")?.ToLowerInvariant();
        var availability = GetString(root, "availability");
        var canDownload = root.TryGetProperty("can_download", out var canDownloadValue) &&
                          canDownloadValue.ValueKind == JsonValueKind.True;

        if (!string.Equals(id, expectedId, StringComparison.Ordinal))
            throw new InvalidDataException("ID файла в Pixeldrain metadata не совпадает с запрошенным.");
        if (string.IsNullOrWhiteSpace(name) ||
            !ManualPackageImportService.IsSupportedImportExtension(Path.GetFileName(name)) ||
            name != Path.GetFileName(name))
            throw new InvalidDataException("Pixeldrain файл не имеет поддерживаемого установочного расширения.");
        if (size <= 0 || size > 16L * 1024 * 1024 * 1024)
            throw new InvalidDataException("Размер Pixeldrain файла равен нулю или превышает лимит ISO 16 GiB.");
        if (sha256 is null || !Regex.IsMatch(sha256, "^[a-f0-9]{64}$"))
            throw new InvalidDataException("Pixeldrain не вернул корректную независимую SHA-256.");
        if (!string.IsNullOrEmpty(availability) || !canDownload)
            throw new InvalidDataException("Pixeldrain не разрешает прямую загрузку этого файла (CAPTCHA, лимит, блокировка или недоступность). Откройте страницу в браузере и при необходимости импортируйте файл вручную; обход ограничений не выполняется.");

        return new PixeldrainFileReference(
            id, name, size, sha256,
            new Uri(BaseUri, $"api/file/{Uri.EscapeDataString(id)}?download"));
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static async Task<string> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken ct)
    {
        if (content.Headers.ContentLength is long declared && declared > maximumBytes)
            throw new InvalidDataException("Pixeldrain metadata response exceeds the allowed size.");
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException("Pixeldrain metadata response exceeds the allowed size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }

    public void Dispose() => _http.Dispose();
}
