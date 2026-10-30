namespace LocaLizard.Core;

public static class SecurityPolicy
{
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "download.windowsupdate.com", "www.download.windowsupdate.com",
        "catalog.s.download.windowsupdate.com", "dl.delivery.mp.microsoft.com",
        "tlu.dl.delivery.mp.microsoft.com", "download.microsoft.com"
    };

    public static bool IsCatalogUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host is "www.catalog.update.microsoft.com" or "catalog.update.microsoft.com" &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsExactMicrosoftCdnHttp(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp &&
        uri.Host is "dl.delivery.mp.microsoft.com" or "tlu.dl.delivery.mp.microsoft.com" &&
        uri.AbsolutePath.StartsWith("/filestreamingservice/files/", StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.UserInfo);

    public static bool IsTrustedPackageUrl(Uri uri) =>
        uri.IsAbsoluteUri && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Scheme == Uri.UriSchemeHttps && DownloadHosts.Contains(uri.Host) || IsExactMicrosoftCdnHttp(uri));

    public static void EnsureSafeFileName(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException($"Недопустимое имя файла: {name}");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы рабочей папки.");
    }
}
