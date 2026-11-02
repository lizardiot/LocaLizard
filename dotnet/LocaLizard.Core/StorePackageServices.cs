using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocaLizard.Core;

public sealed record AppxDependency(string Name, string Publisher, Version? MinVersion);
public sealed record AppxPackageInfo(
    string FilePath,
    string Name,
    string Publisher,
    Version Version,
    string Architecture,
    IReadOnlyList<string> Languages,
    IReadOnlyList<AppxDependency> Dependencies,
    bool IsBundle,
    IReadOnlyList<string>? IncludedArchitectures = null);

public sealed class AppxPackageInspector
{
    public AppxPackageInfo Inspect(string packagePath)
    {
        if (!File.Exists(packagePath)) throw new FileNotFoundException("APPX/MSIX package not found.", packagePath);
        var extension = Path.GetExtension(packagePath);
        if (!new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported APPX/MSIX package extension.");

        using var archive = ZipFile.OpenRead(packagePath);
        var bundleEntry = archive.Entries.FirstOrDefault(x =>
            x.FullName.Equals("AppxMetadata/AppxBundleManifest.xml", StringComparison.OrdinalIgnoreCase));
        var appxEntry = archive.Entries.FirstOrDefault(x =>
            x.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
        var isBundle = bundleEntry is not null;
        var manifest = isBundle ? bundleEntry : appxEntry;
        if (manifest is null) throw new InvalidDataException("APPX/MSIX has no supported package manifest.");
        using var stream = manifest.Open();
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        using var reader = XmlReader.Create(stream, settings);
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new InvalidDataException("APPX manifest has no root element.");
        var identity = root.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == "Identity")
            ?? throw new InvalidDataException("APPX manifest has no Identity element.");
        var name = (string?)identity.Attribute("Name") ?? "";
        var publisher = (string?)identity.Attribute("Publisher") ?? "";
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher))
            throw new InvalidDataException("APPX Identity must specify Name and Publisher.");
        var versionText = (string?)identity.Attribute("Version") ?? "0.0.0.0";
        if (!Version.TryParse(versionText, out var version))
            throw new InvalidDataException($"APPX package version is invalid: {versionText}");
        var architectureText = (string?)identity.Attribute("ProcessorArchitecture") ??
                               (string?)identity.Attribute("Architecture") ?? "neutral";
        var architecture = NormalizeAppxArchitecture(architectureText);

        var languages = document.Descendants()
            .Where(x => x.Name.LocalName is "Resource" or "ResourcePackage")
            .Select(x => (string?)x.Attribute("Language") ?? (string?)x.Attribute("LanguageTag"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        var dependencies = ParseDependencies(document).ToList();
        var includedArchitectures = new List<string>();
        if (isBundle)
        {
            foreach (var nestedEntry in archive.Entries.Where(x =>
                         Path.GetExtension(x.FullName).Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
                         Path.GetExtension(x.FullName).Equals(".msix", StringComparison.OrdinalIgnoreCase)))
            {
                if (nestedEntry.Length > 512L * 1024 * 1024)
                    throw new InvalidDataException("Nested Appx payload is too large for safe manifest inspection.");
                using var nestedInput = nestedEntry.Open();
                using var memory = new MemoryStream();
                nestedInput.CopyTo(memory);
                memory.Position = 0;
                using var nestedArchive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
                var nestedManifest = nestedArchive.Entries.FirstOrDefault(x =>
                    x.FullName.Equals("AppxManifest.xml", StringComparison.OrdinalIgnoreCase));
                if (nestedManifest is null) continue;
                using var nestedStream = nestedManifest.Open();
                using var nestedReader = XmlReader.Create(nestedStream, settings);
                var nestedDocument = XDocument.Load(nestedReader);
                var nestedIdentity = (nestedDocument.Root?.DescendantsAndSelf() ?? Enumerable.Empty<XElement>())
                    .FirstOrDefault(x => x.Name.LocalName == "Identity");
                var nestedArchitecture = NormalizeAppxArchitecture(
                    (string?)nestedIdentity?.Attribute("ProcessorArchitecture") ?? "neutral");
                includedArchitectures.Add(nestedArchitecture);
                languages.AddRange(nestedDocument.Descendants()
                    .Where(x => x.Name.LocalName is "Resource" or "ResourcePackage")
                    .Select(x => (string?)x.Attribute("Language") ?? (string?)x.Attribute("LanguageTag"))
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!));
                dependencies.AddRange(ParseDependencies(nestedDocument));
            }
        }

        return new AppxPackageInfo(Path.GetFullPath(packagePath), name, publisher, version,
            architecture, languages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            dependencies.DistinctBy(x => x.Name + "|" + x.Publisher, StringComparer.OrdinalIgnoreCase).ToArray(),
            isBundle, includedArchitectures.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<AppxDependency> ParseDependencies(XDocument document) =>
        document.Descendants().Where(x => x.Name.LocalName == "PackageDependency")
            .Select(x =>
            {
                var depName = (string?)x.Attribute("Name") ?? "";
                var depPublisher = (string?)x.Attribute("Publisher") ?? "";
                var minText = (string?)x.Attribute("MinVersion");
                Version? min = Version.TryParse(minText, out var parsed) ? parsed : null;
                return new AppxDependency(depName, depPublisher, min);
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name));

    public static string NormalizeAppxArchitecture(string? architecture)
    {
        var value = (architecture ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "neutral" or "msil" => "neutral",
            "x64" or "amd64" => "x64",
            "x86" or "i386" => "x86",
            "arm64" or "aarch64" => "arm64",
            _ => value
        };
    }

    public static bool IsRussianMicrosoftLxp(AppxPackageInfo package, string architecture) =>
        package.Name.Contains("LanguageExperiencePack", StringComparison.OrdinalIgnoreCase) &&
        package.Languages.Contains("ru-RU", StringComparer.OrdinalIgnoreCase) &&
        package.Publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
        (package.IsBundle
            ? package.IncludedArchitectures?.Any(x => x == "neutral" ||
                x.Equals(BuildNumbers.Architecture(architecture), StringComparison.OrdinalIgnoreCase)) == true
            : package.Architecture == "neutral" ||
                package.Architecture.Equals(BuildNumbers.Architecture(architecture), StringComparison.OrdinalIgnoreCase));
}

public sealed record AppxDependencyResolution(
    bool IsComplete,
    IReadOnlyList<string> DependencyPaths,
    IReadOnlyList<string> MissingDependencies);

public sealed class AppxDependencyResolver(AppxPackageInspector? inspector = null)
{
    private readonly AppxPackageInspector _inspector = inspector ?? new AppxPackageInspector();

    public AppxDependencyResolution Resolve(
        AppxPackageInfo package, IEnumerable<string> availablePackages, string architecture)
    {
        var inventory = new List<AppxPackageInfo>();
        foreach (var path in availablePackages)
        {
            try
            {
                var info = _inspector.Inspect(path);
                if (info.Publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase) &&
                    (info.Architecture == "neutral" ||
                     info.Architecture.Equals(BuildNumbers.Architecture(architecture), StringComparison.OrdinalIgnoreCase)))
                    inventory.Add(info);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                // Invalid or unrelated packages are not eligible dependency candidates.
            }
        }

        var resolved = new List<string>();
        var missing = new List<string>();
        foreach (var dependency in package.Dependencies)
        {
            var match = inventory.FirstOrDefault(x =>
                x.Name.Equals(dependency.Name, StringComparison.OrdinalIgnoreCase) &&
                x.Publisher.Equals(dependency.Publisher, StringComparison.OrdinalIgnoreCase) &&
                (dependency.MinVersion is null || x.Version >= dependency.MinVersion));
            if (match is null) missing.Add(dependency.Name);
            else resolved.Add(match.FilePath);
        }
        return new AppxDependencyResolution(missing.Count == 0, resolved, missing);
    }
}

public sealed class StoreProductResolver(HttpClient? httpClient = null) : IDisposable
{
    private const string StoreMetadataEndpoint = "https://store.rg-adguard.net/api/GetFiles";
    private readonly HttpClient _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };

    public async Task<IReadOnlyList<LanguageResourceCandidate>> ResolveRussianLxpAsync(
        string productId, string architecture, int buildFamily, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(productId) ||
            !Regex.IsMatch(productId.Trim(), @"^[A-Za-z0-9][A-Za-z0-9._-]{7,63}$"))
            throw new InvalidDataException("Microsoft Store Product ID has an invalid format.");
        var normalized = BuildNumbers.Architecture(architecture);
        if (!BuildNumbers.IsSupportedArchitecture(normalized))
            throw new InvalidDataException("Unsupported architecture for Store package discovery.");

        if (!SecurityPolicy.IsTrustedMetadataUrl(new Uri(StoreMetadataEndpoint)))
            throw new InvalidDataException("Store metadata endpoint is not allow-listed.");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["type"] = "ProductId",
            ["url"] = productId.Trim(),
            ["ring"] = "Retail",
            ["lang"] = "en-US"
        });
        using var response = await _http.PostAsync(StoreMetadataEndpoint, form, ct);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri ||
            !SecurityPolicy.IsTrustedMetadataUrl(finalUri))
            throw new InvalidDataException("Store metadata request redirected outside the exact allow-listed endpoint.");
        var html = await response.Content.ReadAsStringAsync(ct);
        if (html.Length > 8 * 1024 * 1024) throw new InvalidDataException("Store metadata response is too large.");

        var matches = Regex.Matches(html, "(?is)href\\s*=\\s*[\\"'](?<url>https?://[^\\"']+)[\\"'][^>]*>(?<text>.*?)</a>");
        var results = new List<LanguageResourceCandidate>();
        foreach (Match match in matches)
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value).Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                !SecurityPolicy.IsTrustedPackageUrl(uri) || uri.Scheme != Uri.UriSchemeHttps)
                continue;
            var name = Path.GetFileName(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(name) ||
                !new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" }
                    .Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase) ||
                !name.Contains("ru-ru", StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new LanguageResourceCandidate(
                $"store:{productId}:{name}", "microsoft-store-metadata", name,
                LanguageResourceType.LocalExperiencePack, "ru-RU", normalized, buildFamily,
                "Client", name, DownloadUri: uri,
                TrustLevel: ResourceTrustLevel.ThirdPartyMetadataMicrosoftFile,
                Notes: "RG-Adguard is used only to resolve Microsoft CDN metadata. Package signature and AppxManifest must be verified before provisioning."));
        }
        return results.DistinctBy(x => x.DownloadUri!.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public void Dispose() => _http.Dispose();
}

public sealed class StorePackageDownloader(HttpClient? httpClient = null)
{
    private readonly HttpClient _http = httpClient ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    public async Task<LanguageResourceCandidate> DownloadVerifiedAsync(
        LanguageResourceCandidate candidate, string repositoryRoot, string architecture,
        PackageSignatureVerifier signatures, ManagedRepositoryService repository, CancellationToken ct)
    {
        if (candidate.ResourceType != LanguageResourceType.LocalExperiencePack ||
            candidate.DownloadUri is null || !SecurityPolicy.IsTrustedPackageUrl(candidate.DownloadUri) ||
            candidate.DownloadUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Only allow-listed Microsoft HTTPS LXP downloads are accepted.");

        Directory.CreateDirectory(repositoryRoot);
        var fileName = Path.GetFileName(candidate.DownloadUri.AbsolutePath);
        SecurityPolicy.EnsureSafeFileName(repositoryRoot, fileName);
        var destination = Path.Combine(repositoryRoot, fileName);
        var temporary = destination + ".download";
        try
        {
            var current = candidate.DownloadUri;
            HttpResponseMessage? final = null;
            for (var redirects = 0; redirects <= 6; redirects++)
            {
                if (current.Scheme != Uri.UriSchemeHttps || !SecurityPolicy.IsTrustedPackageUrl(current))
                    throw new InvalidDataException($"Blocked Store package redirect: {current}");
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)response.StatusCode is >= 300 and <= 399)
                {
                    var location = response.Headers.Location ??
                        throw new InvalidDataException("Store package redirect has no Location.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    response.Dispose();
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri is not { } finalUri ||
                    finalUri.Scheme != Uri.UriSchemeHttps || !SecurityPolicy.IsTrustedPackageUrl(finalUri))
                    throw new InvalidDataException("Store package redirect did not end at an allow-listed Microsoft HTTPS CDN.");
                final = response;
                current = finalUri;
                break;
            }
            if (final is null) throw new InvalidDataException("Too many Store package redirects.");
            using (final)
            {
                var size = final.Content.Headers.ContentLength;
                if (size is null or <= 0 || size > 2L * 1024 * 1024 * 1024)
                    throw new InvalidDataException("Store package size is absent or exceeds the 2 GiB limit.");
                await using var input = await final.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[1024 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > size || total > 2L * 1024 * 1024 * 1024)
                        throw new InvalidDataException("Store package exceeded its advertised size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                await output.FlushAsync(ct);
                if (total != size) throw new InvalidDataException("Store package size mismatch.");
            }

            var signature = await signatures.VerifyMicrosoftAsync(temporary, ct);
            if (!signature.IsValid)
                throw new InvalidDataException("Store package is not signed by Microsoft: " + signature.Error);
            var inspector = new AppxPackageInspector();
            var package = inspector.Inspect(temporary);
            if (!AppxPackageInspector.IsRussianMicrosoftLxp(package, architecture))
                throw new InvalidDataException("AppxManifest is not a Microsoft ru-RU Language Experience Pack for the requested architecture.");
            File.Move(temporary, destination, true);
            var resolved = candidate with { FileName = fileName, LocalPath = destination,
                TrustLevel = ResourceTrustLevel.MicrosoftSigned,
                Notes = $"Signature: {signature.Subject}; thumbprint: {signature.Thumbprint}; Appx identity: {package.Name} {package.Version}" };
            await repository.RegisterAsync(repositoryRoot, destination, resolved, signature, ct);
            return resolved;
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }
}

public sealed record LxpProvisionResult(string Package, string Status, string? Details = null);

public sealed class LocalExperiencePackService(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<IReadOnlyList<LxpProvisionResult>> ProvisionAsync(
        string imageMount, IEnumerable<string>? lxpRoots, string architecture, CancellationToken ct, Action<string>? log = null)
    {
        var roots = (lxpRoots ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0) return [];
        var inspector = new AppxPackageInspector();
        var files = roots.SelectMany(root => AdkBootstrapper.EnumerateFilesSafely(root, "*"))
            .Where(x => new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" }
                .Contains(Path.GetExtension(x), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var packages = new List<AppxPackageInfo>();
        var lxpPackages = new List<AppxPackageInfo>();
        var results = new List<LxpProvisionResult>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var signature = await new PackageSignatureVerifier(_runner).VerifyMicrosoftAsync(file, ct);
                if (!signature.IsValid)
                {
                    results.Add(new LxpProvisionResult(Path.GetFileName(file), "signature_invalid", signature.Error));
                    continue;
                }
                var info = inspector.Inspect(file);
                if (!info.Publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase))
                    continue;
                var architectureMatches = info.Architecture == "neutral" ||
                    info.Architecture.Equals(BuildNumbers.Architecture(architecture), StringComparison.OrdinalIgnoreCase) ||
                    info.IncludedArchitectures?.Any(x => x == "neutral" ||
                        x.Equals(BuildNumbers.Architecture(architecture), StringComparison.OrdinalIgnoreCase)) == true;
                if (!architectureMatches) continue;
                packages.Add(info);
                if (AppxPackageInspector.IsRussianMicrosoftLxp(info, architecture))
                    lxpPackages.Add(info);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
            {
                results.Add(new LxpProvisionResult(Path.GetFileName(file), "invalid_package", ex.Message));
            }
        }

        foreach (var package in lxpPackages)
        {
            ct.ThrowIfCancellationRequested();
            var resolution = new AppxDependencyResolver(inspector)
                .Resolve(package, packages.Where(x => x.FilePath != package.FilePath).Select(x => x.FilePath), architecture);
            if (!resolution.IsComplete)
            {
                results.Add(new LxpProvisionResult(Path.GetFileName(package.FilePath), "dependencies_missing",
                    string.Join(", ", resolution.MissingDependencies)));
                continue;
            }
            var license = roots.Select(root => FindLicense(root, package.FilePath)).FirstOrDefault(path => path is not null);
            if (license is null)
            {
                results.Add(new LxpProvisionResult(Path.GetFileName(package.FilePath), "license_required",
                    "Для офлайн-провижининга нужен соответствующий License XML."));
                continue;
            }
            var args = new List<string>
            {
                $"/Image:{imageMount}", "/Add-ProvisionedAppxPackage",
                $"/PackagePath:{package.FilePath}", $"/LicensePath:{license}", "/Region:all"
            };
            foreach (var dependency in resolution.DependencyPaths)
                args.Add($"/DependencyPackagePath:{dependency}");
            var result = await _runner.RunAsync("dism.exe", args,
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(45));
            var status = result.ExitCode == 0 ? "provisioned" : "failed";
            results.Add(new LxpProvisionResult(Path.GetFileName(package.FilePath), status,
                result.ExitCode == 0 ? null : $"DISM exit code {result.ExitCode}"));
            log?.Invoke($"LXP {package.Name}: {status}.");
        }
        return results;
    }

    private static string? FindLicense(string root, string packagePath)
    {
        var stem = Path.GetFileNameWithoutExtension(packagePath);
        var candidates = new List<string>
        {
            Path.Combine(root, stem + ".xml"),
            Path.Combine(root, "License", stem + ".xml")
        };
        candidates.AddRange(AdkBootstrapper.EnumerateFilesSafely(root, "*.xml")
            .Where(path =>
            {
                var name = Path.GetFileName(path);
                return name.Contains("License", StringComparison.OrdinalIgnoreCase) &&
                       (name.Contains(stem, StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("LanguageExperiencePackru-RU", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("ru-ru", StringComparison.OrdinalIgnoreCase));
            }));
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024 };
                using var reader = XmlReader.Create(candidate, settings);
                var document = XDocument.Load(reader);
                if (document.Root is null) continue;
                var rootName = document.Root.Name.LocalName;
                if (rootName.Contains("License", StringComparison.OrdinalIgnoreCase) ||
                    document.Descendants().Any(x => x.Name.LocalName.Contains("License", StringComparison.OrdinalIgnoreCase)))
                    return candidate;
            }
            catch (Exception ex) when (ex is IOException or XmlException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
