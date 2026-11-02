using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace LocaLizard.Core;

public sealed record DismPackageIdentity(
    string Identity,
    string PackageName,
    string Architecture,
    string Language,
    int BuildFamily,
    string ProductFamily);

public sealed class DismPackageIdentityInspector(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<DismPackageIdentity> InspectLanguagePackAsync(
        string packagePath, LanguageResourceRequest request, CancellationToken ct)
    {
        string identity;
        if (Path.GetFileName(packagePath).Equals("update.mum", StringComparison.OrdinalIgnoreCase))
        {
            identity = ReadMumIdentity(packagePath);
        }
        else
        {
            var result = await _runner.RunAsync("dism.exe",
                ["/Get-PackageInfo", $"/PackagePath:{Path.GetFullPath(packagePath)}", "/English"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(10));
            var match = Regex.Match(result.StandardOutput,
                @"(?im)^\s*Package Identity\s*:\s*(.+?)\s*$");
            if (!match.Success)
                throw new InvalidDataException("DISM не вернул Package Identity для языкового CAB.");
            identity = match.Groups[1].Value.Trim();
        }

        var lower = identity.ToLowerInvariant();
        var server = lower.Contains("server-languagepack") || lower.Contains("server-language-pack");
        var client = lower.Contains("client-languagepack") || lower.Contains("client-language-pack");
        if (!server && !client)
            throw new InvalidDataException($"CAB identity не является Client/Server Language Pack: {identity}");
        if (server != PackageCompatibilityService.IsServer(request.ProductFamily))
            throw new InvalidDataException("Client/Server Language Pack не соответствует семейству продукта образа.");
        if (!lower.Contains("ru-ru", StringComparison.Ordinal))
            throw new InvalidDataException("Пакет не содержит language identity ru-RU.");

        var architecture = InferArchitecture(lower);
        if (!string.Equals(architecture, request.Architecture, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Архитектура пакета {architecture} не совпадает с {request.Architecture}.");

        var versionMatch = Regex.Match(identity, @"(?<!\d)10\.0\.(\d{5})\.");
        var build = versionMatch.Success && int.TryParse(versionMatch.Groups[1].Value, out var parsed) ? parsed : 0;
        if (build > 0 && !PackageCompatibilityService.IsCompatiblePackageBuild(request.BuildFamily, build))
            throw new InvalidDataException($"Package Identity build {build} does not match target build {request.BuildFamily}.");

        return new DismPackageIdentity(identity, server ? "ServerLanguagePack" : "ClientLanguagePack",
            architecture!, "ru-RU", build == 0 ? request.BuildFamily : build, request.ProductFamily);
    }

    public async Task<string> NormalizeFileNameAsync(
        string packagePath, LanguageResourceRequest request, CancellationToken ct, string? targetRoot = null)
    {
        var identity = await InspectLanguagePackAsync(packagePath, request, ct);
        var prefix = identity.ProductFamily == "Server"
            ? "Microsoft-Windows-Server-LanguagePack-Package"
            : "Microsoft-Windows-Client-LanguagePack-Package";
        var currentName = Path.GetFileName(packagePath);
        if (currentName.Equals("update.mum", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Path.GetDirectoryName(packagePath)!;
            var targetName = $"{prefix}_ru-ru_{identity.Architecture}_{identity.BuildFamily}_extracted";
            var destinationRoot = string.IsNullOrWhiteSpace(targetRoot)
                ? Path.GetDirectoryName(parent)!
                : Path.GetFullPath(targetRoot);
            Directory.CreateDirectory(destinationRoot);
            var target = Path.Combine(destinationRoot, targetName);
            if (!parent.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(parent, target);
                packagePath = Path.Combine(target, "update.mum");
            }
            return packagePath;
        }

        var extension = Path.GetExtension(packagePath);
        var safeName = $"{prefix}_ru-ru_{identity.Architecture}_{identity.BuildFamily}{extension}";
        SecurityPolicy.EnsureSafeFileName(Path.GetDirectoryName(packagePath)!, safeName);
        var targetFile = Path.Combine(Path.GetDirectoryName(packagePath)!, safeName);
        if (!Path.GetFullPath(packagePath).Equals(Path.GetFullPath(targetFile), StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(targetFile)) File.Delete(targetFile);
            File.Move(packagePath, targetFile);
        }
        return targetFile;
    }

    private static string ReadMumIdentity(string path)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 };
        using var reader = XmlReader.Create(path, settings);
        var document = XDocument.Load(reader);
        var assembly = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "assemblyIdentity")
            ?? throw new InvalidDataException("update.mum does not contain assemblyIdentity.");
        var name = (string?)assembly.Attribute("name") ?? "";
        var architecture = (string?)assembly.Attribute("processorArchitecture") ?? "";
        var version = (string?)assembly.Attribute("version") ?? "";
        var language = (string?)assembly.Attribute("language") ?? "";
        return $"{name}~{architecture}~{language}~{version}";
    }

    private static string? InferArchitecture(string identity)
    {
        if (Regex.IsMatch(identity, @"(?i)(?:~|_|-)arm64(?:~|_|-|$)")) return "arm64";
        if (Regex.IsMatch(identity, @"(?i)(?:~|_|-)amd64(?:~|_|-|$)") ||
            Regex.IsMatch(identity, @"(?i)(?:~|_|-)x64(?:~|_|-|$)")) return "x64";
        if (Regex.IsMatch(identity, @"(?i)(?:~|_|-)x86(?:~|_|-|$)")) return "x86";
        return null;
    }
}
