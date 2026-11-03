using System.Text.RegularExpressions;
namespace LocaLizard.Core;

public static class SecurityPolicy
{
    private static readonly HashSet<string> DownloadHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "download.windowsupdate.com",
        "wsus.ds.download.windowsupdate.com",
        "catalog.s.download.windowsupdate.com",
        "dl.delivery.mp.microsoft.com",
        "tlu.dl.delivery.mp.microsoft.com",
        "download.microsoft.com",
        "software-static.download.prss.microsoft.com"
    };

    private static readonly HashSet<string> MetadataHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.uupdump.net",
        "api.github.com",
        "raw.githubusercontent.com",
        "gist.githubusercontent.com",
        "store.rg-adguard.net",
        "files.rg-adguard.net"
    };

    public static bool IsCatalogUrl(Uri uri) =>
        IsSafeHttpsUri(uri) &&
        (uri.Host.Equals("www.catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("catalog.update.microsoft.com", StringComparison.OrdinalIgnoreCase));

    public static bool IsExactMicrosoftCdnHttp(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp &&
        (uri.Host.Equals("dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("tlu.dl.delivery.mp.microsoft.com", StringComparison.OrdinalIgnoreCase)) &&
        uri.AbsolutePath.StartsWith("/filestreamingservice/files/", StringComparison.OrdinalIgnoreCase) &&
        HasSafeAuthority(uri);

    public static bool IsTrustedPackageUrl(Uri uri) =>
        IsSafeHttpsUri(uri) && DownloadHosts.Contains(uri.Host) ||
        IsExactMicrosoftCdnHttp(uri);

    public static bool IsTrustedMetadataUrl(Uri uri)
    {
        if (!IsSafeHttpsUri(uri) || !MetadataHosts.Contains(uri.Host)) return false;
        if (uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsoluteUri.Equals(
                "https://api.github.com/gists/87f30cffa70ea6899177f536a8dc788f/c9ec1ecb985a62b7610263254fb2e2ccb2c968c1",
                StringComparison.OrdinalIgnoreCase);
        if (uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/abbodi1406/BatUtil/master/W10MUI/", StringComparison.Ordinal) ||
                   uri.AbsolutePath.StartsWith("/OSDeploy/OSBuilder.Public/master/Content/Updates/", StringComparison.Ordinal) ||
                   uri.AbsolutePath.StartsWith("/microsoft/WhatTheHack/53868d7819be6c83a507ecd6c2971a4b1d4de286/037-AzureVirtualDesktop/Coach/Solutions/04-installLanguagePacks.ps1", StringComparison.Ordinal);
        if (uri.Host.Equals("gist.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.StartsWith("/fydexx/020e34f98df023d5970f353a443fe396/raw/b30ee1d71b1f680d6b24de53a9aa745157c877a9/language_packs.txt", StringComparison.Ordinal);
        if (uri.Host.Equals("store.rg-adguard.net", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Equals("/api/GetFiles", StringComparison.OrdinalIgnoreCase);
        if (uri.Host.Equals("files.rg-adguard.net", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath.Equals("/search", StringComparison.OrdinalIgnoreCase) ||
                   Regex.IsMatch(uri.AbsolutePath, @"(?i)^/file/[a-f0-9-]{8,64}/?$");
        return uri.Host.Equals("api.uupdump.net", StringComparison.OrdinalIgnoreCase) &&
               uri.AbsolutePath.StartsWith("/", StringComparison.Ordinal);
    }

    public static bool IsTrustedMicrosoftMediaUrl(Uri uri) =>
        IsSafeHttpsUri(uri) &&
        uri.Host.Equals("software-download.microsoft.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.StartsWith("/download/pr/", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) &&
        !uri.AbsolutePath.Contains("..", StringComparison.Ordinal);

    public static bool IsTrustedRedirect(Uri initial, Uri next) =>
        initial.IsAbsoluteUri && next.IsAbsoluteUri &&
        initial.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttps &&
        IsTrustedPackageUrl(next);

    private static bool IsSafeHttpsUri(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Port == 443 && HasSafeAuthority(uri);

    private static bool HasSafeAuthority(Uri uri) =>
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) &&
        uri.HostNameType == UriHostNameType.Dns && uri.Port is 80 or 443;

    public static void EnsureSafeFileName(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Недопустимое имя файла: {name}");
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Путь выходит за пределы рабочей папки.");
    }
}
