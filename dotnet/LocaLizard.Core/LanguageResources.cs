using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public enum LanguageResourceType
{
    ClientLanguagePack,
    ServerLanguagePack,
    FeatureOnDemand,
    LocalExperiencePack,
    LanguageInterfacePack,
    WinPELanguagePack,
    WinRELanguagePack,
    LanguageFont,
    LanguageCapability,
    LanguageUpdate
}

public enum ResourceTrustLevel
{
    MicrosoftSigned,
    MicrosoftHostedCatalog,
    ThirdPartyMetadataMicrosoftFile,
    LocalUnverified
}

public sealed record LanguageResourceRequest(
    int BuildFamily,
    string Architecture,
    string Language = "ru-RU",
    string ProductFamily = "Client",
    LanguageResourceType? ResourceType = null);

public sealed record LanguageResourceCandidate(
    string Id,
    string SourceId,
    string DisplayName,
    LanguageResourceType ResourceType,
    string Language,
    string Architecture,
    int? BuildFamily,
    string ProductFamily,
    string FileName,
    Uri? DownloadUri = null,
    string? LocalPath = null,
    string? Sha1 = null,
    long? Size = null,
    ResourceTrustLevel TrustLevel = ResourceTrustLevel.LocalUnverified,
    string? Notes = null);

public interface ILanguageResourceProvider
{
    string SourceId { get; }
    Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken);
}

public static class PackageCompatibilityService
{
    private static readonly IReadOnlyDictionary<int, string> ClientReleaseNames =
        new Dictionary<int, string>
        {
            [10240] = "Windows 10 IoT Enterprise LTSB 2015",
            [14393] = "Windows 10 IoT Enterprise LTSB 2016",
            [17763] = "Windows 10 IoT Enterprise LTSC 2019",
            [19044] = "Windows 10 IoT Enterprise LTSC 2021",
            [22000] = "Windows 11 21H2",
            [22621] = "Windows 11 22H2",
            [22631] = "Windows 11 23H2 (22621 enablement family)",
            [26100] = "Windows 11 IoT Enterprise LTSC 2024"
        };

    private static readonly IReadOnlyDictionary<int, string> ServerReleaseNames =
        new Dictionary<int, string>
        {
            [14393] = "Windows Server 2016",
            [17763] = "Windows Server 2019",
            [20348] = "Windows Server 2022",
            [26100] = "Windows Server 2025"
        };

    public static bool IsKnownBuildFamily(int buildFamily, string productFamily) =>
        (IsServer(productFamily) ? ServerReleaseNames : ClientReleaseNames).ContainsKey(buildFamily);

    public static string? GetReleaseName(int buildFamily, string productFamily) =>
        (IsServer(productFamily) ? ServerReleaseNames : ClientReleaseNames)
            .TryGetValue(buildFamily, out var name) ? name : null;

    public static bool IsCompatiblePackageBuild(int targetBuild, int packageBuild) =>
        targetBuild == packageBuild || targetBuild == 22631 && packageBuild == 22621;

    public static bool IsServer(string? productFamily) =>
        string.Equals(productFamily, "Server", StringComparison.OrdinalIgnoreCase);

    public static string ProductFamily(ImageInfo image) =>
        image.Name.Contains("server", StringComparison.OrdinalIgnoreCase) ||
        image.Name.Contains("Azure Stack HCI", StringComparison.OrdinalIgnoreCase)
            ? "Server" : "Client";

    public static LanguageResourceRequest CreateRequest(ImageInfo image, LanguageResourceType? type = null)
    {
        var architecture = image.NormalizedArchitecture;
        var productFamily = ProductFamily(image);
        if (image.BuildFamily == 0 || !BuildNumbers.IsSupportedArchitecture(architecture))
            throw new InvalidDataException($"Нельзя определить совместимость для {image.Name}, {image.Version}, {image.Architecture}.");
        if (!IsKnownBuildFamily(image.BuildFamily, productFamily))
            throw new InvalidDataException(
                $"Сборка {image.BuildFamily} ({productFamily}) отсутствует в матрице поддерживаемых выпусков. Пакеты по близкому номеру не подбираются.");
        return new LanguageResourceRequest(image.BuildFamily, architecture, "ru-RU", productFamily, type);
    }

    public static bool IsCompatible(LanguageResourceCandidate candidate, LanguageResourceRequest request)
    {
        if (!string.Equals(request.Language, "ru-RU", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(candidate.Language, "ru-RU", StringComparison.OrdinalIgnoreCase))
            return false;
        if (request.ResourceType is not null && candidate.ResourceType != request.ResourceType) return false;
        if (!string.Equals(BuildNumbers.Architecture(candidate.Architecture), request.Architecture,
                StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.BuildFamily != request.BuildFamily) return false;
        if (!string.Equals(candidate.ProductFamily, request.ProductFamily, StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsKnownBuildFamily(request.BuildFamily, request.ProductFamily)) return false;
        if (candidate.LocalPath is null && candidate.DownloadUri is null) return false;
        if (candidate.DownloadUri is not null && !SecurityPolicy.IsTrustedPackageUrl(candidate.DownloadUri)) return false;
        return candidate.ResourceType switch
        {
            LanguageResourceType.ClientLanguagePack => !PackageNameLooksServer(candidate.FileName),
            LanguageResourceType.ServerLanguagePack => PackageNameLooksServer(candidate.FileName),
            LanguageResourceType.FeatureOnDemand => IsFodPackage(candidate.FileName),
            LanguageResourceType.LocalExperiencePack => IsLxpPackage(candidate.FileName),
            LanguageResourceType.WinPELanguagePack or LanguageResourceType.WinRELanguagePack =>
                IsWinPeOrWinRePackage(candidate.FileName),
            _ => true
        };
    }

    public static bool PackageNameLooksServer(string? value) =>
        (value ?? "").Contains("server", StringComparison.OrdinalIgnoreCase);

    public static bool IsFodPackage(string? value)
    {
        var name = value ?? "";
        return name.Contains("languagefeatures", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.basic", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.ocr", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.speech", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.handwriting", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.texttospeech", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("languagefeatures-fonts", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("language.fonts", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("winocr", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLxpPackage(string? value) =>
        (value ?? "").Contains("languageexperiencepack", StringComparison.OrdinalIgnoreCase) ||
        (value ?? "").Contains("language-experience-pack", StringComparison.OrdinalIgnoreCase);

    public static bool IsWinPeOrWinRePackage(string? value)
    {
        var name = value ?? "";
        return name.Contains("winpe", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("winre", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("lp.cab", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class ResourceSourceRegistry
{
    private readonly Dictionary<string, ILanguageResourceProvider> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Diagnostic;

    public void Register(ILanguageResourceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (string.IsNullOrWhiteSpace(provider.SourceId))
            throw new ArgumentException("У провайдера должен быть SourceId.", nameof(provider));
        _providers[provider.SourceId] = provider;
    }

    public IReadOnlyList<string> Sources => _providers.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Language, "ru-RU", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LocaLizard поддерживает только ru-RU.");
        var results = new List<LanguageResourceCandidate>();
        foreach (var provider in _providers.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var found = await provider.DiscoverAsync(request, cancellationToken);
                results.AddRange(found.Where(x => PackageCompatibilityService.IsCompatible(x, request)));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or
                                      UnauthorizedAccessException or System.Xml.XmlException or TimeoutException)
            {
                Diagnostic?.Invoke($"Источник {provider.SourceId} пропущен: {ex.Message}");
                // A source outage must not prevent other independent providers from being queried.
            }
        }

        return results
            .GroupBy(x => (x.ResourceType, x.FileName, x.LocalPath, Url: x.DownloadUri?.AbsoluteUri),
                StringTupleComparer.Instance)
            .Select(g => g.OrderBy(x => x.TrustLevel).First())
            .OrderBy(x => x.TrustLevel)
            .ThenBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class StringTupleComparer : IEqualityComparer<(LanguageResourceType ResourceType, string FileName, string? LocalPath, string? Url)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((LanguageResourceType ResourceType, string FileName, string? LocalPath, string? Url) x,
            (LanguageResourceType ResourceType, string FileName, string? LocalPath, string? Url) y) =>
            x.ResourceType == y.ResourceType &&
            string.Equals(x.FileName, y.FileName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.LocalPath, y.LocalPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Url, y.Url, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((LanguageResourceType ResourceType, string FileName, string? LocalPath, string? Url) value) =>
            HashCode.Combine(value.ResourceType, StringComparer.OrdinalIgnoreCase.GetHashCode(value.FileName),
                value.LocalPath is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.LocalPath),
                value.Url is null ? 0 : StringComparer.OrdinalIgnoreCase.GetHashCode(value.Url));
    }
}

public sealed class LocalRepositoryProvider(string root, string sourceId = "local-repository") : ILanguageResourceProvider
{
    public string SourceId { get; } = sourceId;

    public Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(root)) return Task.FromResult<IReadOnlyList<LanguageResourceCandidate>>([]);
        var candidates = new List<LanguageResourceCandidate>();
        foreach (var file in AdkBootstrapper.EnumerateFilesSafely(root, "*"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var lower = name.ToLowerInvariant();
            if (!lower.Contains("ru-ru", StringComparison.Ordinal) &&
                !Path.GetDirectoryName(file)!.Contains("ru-ru", StringComparison.OrdinalIgnoreCase) &&
                !PackageNameLooksServer(name) && !name.Equals("lp.cab", StringComparison.OrdinalIgnoreCase))
                continue;

            var extension = Path.GetExtension(file);
            if (!extension.Equals(".cab", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".esd", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".appx", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".msix", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase))
                continue;

            var resourceType = Classify(file);
            var architecture = InferArchitecture(file) ?? request.Architecture;
            var build = InferBuildFamily(file);
            var productFamily = PackageNameLooksServer(file) ? "Server" : request.ProductFamily;
            candidates.Add(new LanguageResourceCandidate(
                $"local:{Path.GetFullPath(file)}", SourceId, name, resourceType, "ru-RU",
                architecture, build ?? request.BuildFamily, productFamily, name,
                LocalPath: Path.GetFullPath(file), TrustLevel: ResourceTrustLevel.LocalUnverified,
                Notes: "Локальный файл; подпись и совместимость должны быть проверены до DISM."));
        }
        return Task.FromResult<IReadOnlyList<LanguageResourceCandidate>>(candidates);
    }

    private static LanguageResourceType Classify(string file)
    {
        var name = Path.GetFileName(file);
        var path = file.Replace('\\', '/');
        if (Path.GetExtension(name).Equals(".appx", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".msix", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".appxbundle", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".msixbundle", StringComparison.OrdinalIgnoreCase))
            return LanguageResourceType.LocalExperiencePack;
        if (name.Contains("winpe", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/WinPE_OCs/", StringComparison.OrdinalIgnoreCase))
            return LanguageResourceType.WinPELanguagePack;
        if (name.Contains("winre", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/WinRE/", StringComparison.OrdinalIgnoreCase))
            return LanguageResourceType.WinRELanguagePack;
        if (PackageCompatibilityService.IsFodPackage(name) ||
            path.Contains("LanguagesAndOptionalFeatures", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("FeaturesOnDemand", StringComparison.OrdinalIgnoreCase))
            return LanguageResourceType.FeatureOnDemand;
        if (PackageNameLooksServer(name)) return LanguageResourceType.ServerLanguagePack;
        if (WindowsImageService.IsClientLp(name, "x64") ||
            WindowsImageService.IsClientLp(name, "x86") ||
            WindowsImageService.IsClientLp(name, "arm64"))
            return LanguageResourceType.ClientLanguagePack;
        if (name.Equals("lp.cab", StringComparison.OrdinalIgnoreCase))
            return LanguageResourceType.WinPELanguagePack;
        return LanguageResourceType.LanguageInterfacePack;
    }

    private static string? InferArchitecture(string file)
    {
        var name = Path.GetFileName(file);
        foreach (var arch in new[] { "arm64", "amd64", "x64", "x86_64", "x86" })
            if (Regex.IsMatch(name, $@"(?i)(?:^|[~_.-]){Regex.Escape(arch)}(?:$|[~_.-])"))
                return BuildNumbers.Architecture(arch);
        var parent = Path.GetFileName(Path.GetDirectoryName(file));
        if (!string.IsNullOrWhiteSpace(parent) && Regex.IsMatch(parent, @"(?i)(x64|x86|amd64|arm64)"))
            return BuildNumbers.Architecture(parent);
        return null;
    }

    private static int? InferBuildFamily(string file)
    {
        var text = file.Replace('\\', '/');
        var match = Regex.Match(text, @"(?<!\d)(10240|14393|17763|19041|19044|20348|22000|22621|22631|26100)(?:_|\D)");
        if (!match.Success) return null;
        var value = int.Parse(match.Groups[1].Value);
        return value switch { 19041 => 19044, _ => value };
    }
}
