using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public static class FodCapabilityCatalog
{
    public static IReadOnlyList<string> RussianCapabilities { get; } =
    [
        "Language.Basic~~~ru-RU~0.0.1.0",
        "Language.OCR~~~ru-RU~0.0.1.0",
        "Language.Handwriting~~~ru-RU~0.0.1.0",
        "Language.TextToSpeech~~~ru-RU~0.0.1.0",
        "Language.Speech~~~ru-RU~0.0.1.0",
        "Language.Fonts~~~ru-RU~0.0.1.0"
    ];
}

public sealed record FodCapabilityInfo(string Name, string State);

public sealed class FodInventoryService(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<IReadOnlyList<FodCapabilityInfo>> GetImageCapabilitiesAsync(
        string imageMount, CancellationToken ct)
    {
        var result = await _runner.RunAsync("dism.exe",
            [$"/Image:{imageMount}", "/Get-Capabilities", "/English"],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(10));
        return ParseCapabilities(result.StandardOutput);
    }

    internal static IReadOnlyList<FodCapabilityInfo> ParseCapabilities(string output)
    {
        var records = new List<FodCapabilityInfo>();
        var lines = output.Replace("\r", "").Split('\n');
        string? name = null;
        foreach (var line in lines)
        {
            var identity = Regex.Match(line, @"(?i)^\s*Capability Identity\s*:\s*(\S+)");
            if (identity.Success)
            {
                if (name is not null) records.Add(new FodCapabilityInfo(name, "Unknown"));
                name = identity.Groups[1].Value.Trim();
                continue;
            }
            var state = Regex.Match(line, @"(?i)^\s*State\s*:\s*(.+?)\s*$");
            if (state.Success && name is not null)
            {
                records.Add(new FodCapabilityInfo(name, state.Groups[1].Value.Trim()));
                name = null;
            }
        }
        if (name is not null) records.Add(new FodCapabilityInfo(name, "Unknown"));
        return records;
    }

    public static IReadOnlyList<string> FindRussianCapabilityPackages(string sourceRoot, string architecture)
    {
        if (!Directory.Exists(sourceRoot)) return [];
        var arch = BuildNumbers.Architecture(architecture);
        var candidates = new List<string>();
        foreach (var file in AdkBootstrapper.EnumerateFilesSafely(sourceRoot, "*.cab"))
        {
            var name = Path.GetFileName(file);
            if (!name.Contains("ru-ru", StringComparison.OrdinalIgnoreCase) &&
                !file.Contains("ru-ru", StringComparison.OrdinalIgnoreCase)) continue;
            var packageArch = InferArchitecture(file);
            if (packageArch is not null && !string.Equals(packageArch, arch, StringComparison.OrdinalIgnoreCase))
                continue;
            if (PackageCompatibilityService.IsFodPackage(name) ||
                file.Contains("LanguagesAndOptionalFeatures", StringComparison.OrdinalIgnoreCase) ||
                file.Contains("FeaturesOnDemand", StringComparison.OrdinalIgnoreCase))
                candidates.Add(file);
        }
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? InferArchitecture(string file)
    {
        var lower = file.ToLowerInvariant();
        if (lower.Contains("arm64")) return "arm64";
        if (lower.Contains("amd64") || lower.Contains("x64")) return "x64";
        if (Regex.IsMatch(lower, @"(?:_|~|-|\\)x86(?:_|~|-|\\|\.|$)")) return "x86";
        return null;
    }
}

public sealed class FodRepositoryBuilder(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<string> PrepareRepositoryAsync(
        string imageMount, string sourceRoot, string workDirectory, ImageInfo image,
        CancellationToken ct, Action<string>? log = null)
    {
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("FOD source directory not found.");
        var fullSource = Path.GetFullPath(sourceRoot);
        var request = PackageCompatibilityService.CreateRequest(image, LanguageResourceType.FeatureOnDemand);
        var sourcePackages = FodInventoryService.FindRussianCapabilityPackages(fullSource, image.NormalizedArchitecture);
        if (sourcePackages.Count == 0)
            throw new InvalidDataException("В выбранном каталоге не обнаружены русские FOD CAB для указанной архитектуры.");

        var signatureVerifier = new PackageSignatureVerifier(_runner);
        var identityInspector = new FodPackageIdentityInspector(_runner);
        var verifiedPackages = new List<string>();
        foreach (var package in sourcePackages)
        {
            ct.ThrowIfCancellationRequested();
            var signature = await signatureVerifier.VerifyMicrosoftAsync(package, ct);
            if (!signature.IsValid)
                throw new InvalidDataException($"FOD CAB не прошёл проверку подписи Microsoft: {Path.GetFileName(package)} ({signature.Error})");
            _ = await identityInspector.InspectAsync(package, request, ct);
            verifiedPackages.Add(package);
        }

        if (LooksLikeOfficialRepository(fullSource))
        {
            log?.Invoke($"Использую проверенный Microsoft Languages and Optional Features / FOD repository ({verifiedPackages.Count} русских CAB).");
            return fullSource;
        }

        var available = await new FodInventoryService(_runner).GetImageCapabilitiesAsync(imageMount, ct);
        var supported = available
            .Where(x => FodCapabilityCatalog.RussianCapabilities.Contains(x.Name, StringComparer.OrdinalIgnoreCase) &&
                        !x.State.Equals("Installed", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Name).ToArray();
        var requested = supported.Length == 0
            ? FodCapabilityCatalog.RussianCapabilities.ToArray()
            : supported;

        var languageFodOnly = verifiedPackages.All(path =>
            Path.GetFileName(path).Contains("LanguageFeatures", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Contains("WinOcr-FOD-Package", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(path).Contains("Language.Fonts", StringComparison.OrdinalIgnoreCase));
        if (languageFodOnly)
        {
            log?.Invoke("Обнаружены подписанные монолитные языковые FOD ru-RU без satellite packages; использую CAB-папку как источник DISM /Add-Capability.");
            return fullSource;
        }

        var repository = Path.Combine(workDirectory, "fod_repository_ru_ru_" + image.BuildFamily + "_" + image.NormalizedArchitecture);
        Directory.CreateDirectory(repository);
        log?.Invoke($"DISM /Export-Source: готовлю репозиторий ru-RU для {requested.Length} capabilities.");
        foreach (var capability in requested)
        {
            ct.ThrowIfCancellationRequested();
            var export = await _runner.RunAsync("dism.exe",
                [$"/Image:{imageMount}", "/Export-Source", $"/Source:{fullSource}",
                 $"/Target:{repository}", $"/CapabilityName:{capability}"],
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(60));
            if (export.ExitCode != 0)
                throw new InvalidDataException($"DISM /Export-Source не смог подготовить {capability} (код {export.ExitCode}).");
        }
        if (!AdkBootstrapper.EnumerateFilesSafely(repository, "*").Any())
            throw new InvalidDataException("DISM /Export-Source не создал репозиторий компонентов.");
        return repository;
    }

    private static bool LooksLikeOfficialRepository(string root) =>
        Path.GetFileName(root).Equals("LanguagesAndOptionalFeatures", StringComparison.OrdinalIgnoreCase) ||
        Path.GetFileName(root).Equals("FeaturesOnDemand", StringComparison.OrdinalIgnoreCase) ||
        Directory.Exists(Path.Combine(root, "Metadata")) ||
        Directory.Exists(Path.Combine(root, "metadata"));
}
