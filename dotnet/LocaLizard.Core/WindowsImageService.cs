using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed class WindowsImageService(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;
    public List<ImageAudit> Audit { get; } = [];

    public async Task<IReadOnlyList<ImageInfo>> GetImageInfoAsync(string file, CancellationToken ct, string? swmFilePattern = null)
    {
        var summaryArgs = new List<string> { "/Get-WimInfo", $"/WimFile:{file}" };
        if (!string.IsNullOrWhiteSpace(swmFilePattern)) summaryArgs.Add($"/SWMFile:{swmFilePattern}");
        summaryArgs.Add("/English");
        var summary = await _runner.RunAsync("dism.exe", summaryArgs,
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(3));
        var indexes = Regex.Matches(summary.StandardOutput, @"(?im)^\s*Index\s*:\s*(\d+)\s*$")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        var result = new List<ImageInfo>();
        foreach (var index in indexes)
        {
            var detailArgs = new List<string> { "/Get-WimInfo", $"/WimFile:{file}" };
            if (!string.IsNullOrWhiteSpace(swmFilePattern)) detailArgs.Add($"/SWMFile:{swmFilePattern}");
            detailArgs.Add($"/Index:{index}");
            detailArgs.Add("/English");
            var detail = await _runner.RunAsync("dism.exe", detailArgs,
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(3));
            var text = detail.StandardOutput;
            var version = Field(text, "Version") ?? "";
            var revision = Field(text, "ServicePack Build");
            if (!string.IsNullOrWhiteSpace(revision) && !version.EndsWith("." + revision, StringComparison.Ordinal))
                version += "." + revision;
            result.Add(new ImageInfo(index, Field(text, "Name") ?? $"Windows index {index}",
                Field(text, "Architecture") ?? "", version));
        }
        if (result.Count == 0) throw new InvalidDataException($"DISM не обнаружил индексов в {Path.GetFileName(file)}.");
        return result;
    }

    public async Task ServiceAsync(
        string imageFile, ImageInfo image, IReadOnlyList<string> packageCandidates,
        string? fod, UpdatePlan? updates, string work, Action<string>? log, CancellationToken ct,
        Func<string, Task<LocalizationStep>>? serviceWinRe = null,
        string? driverDirectory = null, string? unattendFile = null, bool allowPartialLocalization = false,
        IEnumerable<string>? lxpDirectories = null)
    {
        if (image.BuildFamily == 0 || !BuildNumbers.IsSupportedArchitecture(image.NormalizedArchitecture))
            throw new InvalidDataException($"Не удалось определить сборку/архитектуру индекса {image.Index}.");
        if (updates is not null && (updates.TargetBuildFamily != image.BuildFamily || updates.Architecture != image.NormalizedArchitecture))
            throw new InvalidDataException("Набор обновлений не соответствует сборке/архитектуре индекса.");

        if (!string.IsNullOrWhiteSpace(driverDirectory) && !Directory.Exists(driverDirectory))
            throw new DirectoryNotFoundException("Каталог INF-драйверов не найден.");
        if (!string.IsNullOrWhiteSpace(unattendFile) && !File.Exists(unattendFile))
            throw new FileNotFoundException("Файл unattend.xml не найден.", unattendFile);

        var productFamily = PackageCompatibilityService.ProductFamily(image);
        var candidates = packageCandidates.Where(p => IsLanguagePackForImage(p, image))
            .OrderBy(p => Path.GetFileName(p).Equals("update.mum", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        if (candidates.Length == 0)
            throw new FileNotFoundException($"Не найден совместимый {productFamily} Language Pack ru-RU для {image.NormalizedArchitecture}, build {image.BuildFamily}.");

        var mount = Path.Combine(work, $"mount_install_{image.Index}");
        Directory.CreateDirectory(mount);
        var mounted = false;
        string? selected = null;
        Exception? lastError = null;
        var capabilityResults = new List<CapabilityAudit>();
        try
        {
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                await _runner.RunAsync("dism.exe", ["/Mount-Image", $"/ImageFile:{imageFile}", $"/Index:{image.Index}", $"/MountDir:{mount}", "/CheckIntegrity"],
                    cancellationToken: ct, timeout: TimeSpan.FromMinutes(25));
                mounted = true;
                if (!string.IsNullOrWhiteSpace(driverDirectory))
                {
                    log?.Invoke($"Индекс {image.Index}: интегрирую INF-драйверы из {driverDirectory}.");
                    await _runner.RunAsync("dism.exe",
                        [$"/Image:{mount}", "/Add-Driver", $"/Driver:{Path.GetFullPath(driverDirectory)}", "/Recurse"],
                        cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
                }
                await ApplyUpdatesAsync(mount, updates, "before_language", ct, log);
                log?.Invoke($"Индекс {image.Index}: добавляю базовый языковой пакет {Path.GetFileName(candidate)}.");
                try
                {
                    await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{candidate}", "/NoRestart"],
                        cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
                    selected = candidate;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    log?.Invoke($"DISM отклонил {Path.GetFileName(candidate)}; откатываю попытку.");
                    await DiscardAsync(mount);
                    mounted = false;
                    Directory.Delete(mount, true);
                    Directory.CreateDirectory(mount);
                }
            }
            if (selected is null) throw new InvalidOperationException($"DISM отклонил все языковые пакеты для индекса {image.Index}.", lastError);

            var caps = FodCapabilityCatalog.RussianCapabilities;
            if (string.IsNullOrWhiteSpace(fod))
            {
                capabilityResults.AddRange(caps.Select(x => new CapabilityAudit(x, "not_requested")));
            }
            else if (!Directory.Exists(fod))
            {
                capabilityResults.AddRange(caps.Select(x => new CapabilityAudit(x, "source_missing")));
            }
            else
            {
                try
                {
                    var inventory = await new FodInventoryService(_runner).GetImageCapabilitiesAsync(mount, ct);
                    var available = inventory.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var requested = caps.Where(x => available.Count == 0 || available.Contains(x)).ToArray();
                    capabilityResults.AddRange(caps.Except(requested, StringComparer.OrdinalIgnoreCase)
                        .Select(x => new CapabilityAudit(x, "not_available_in_target")));
                    if (requested.Length > 0)
                    {
                        var repository = await new FodRepositoryBuilder(_runner).PrepareRepositoryAsync(
                            mount, fod, work, image.NormalizedArchitecture, ct, log);
                        foreach (var capability in requested)
                        {
                            ct.ThrowIfCancellationRequested();
                            var result = await _runner.RunAsync("dism.exe",
                                [$"/Image:{mount}", "/Add-Capability", $"/CapabilityName:{capability}", $"/Source:{repository}", "/LimitAccess"],
                                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(30));
                            capabilityResults.Add(new CapabilityAudit(capability,
                                result.ExitCode == 0 ? "installed" : "unavailable", result.ExitCode));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
                {
                    capabilityResults.AddRange(caps.Where(x => capabilityResults.All(c => !c.Name.Equals(x, StringComparison.OrdinalIgnoreCase)))
                        .Select(x => new CapabilityAudit(x, "source_unusable")));
                    log?.Invoke("FOD repository не удалось подготовить; Language Features пропущены: " + ex.Message);
                }
            }

            foreach (var locale in new[]
            {
                "/Set-AllIntl:ru-RU", "/Set-UILang:ru-RU", "/Set-UserLocale:ru-RU",
                "/Set-SysLocale:ru-RU", "/Set-InputLocale:0419:00000419"
            })
                await _runner.RunAsync("dism.exe", [$"/Image:{mount}", locale], cancellationToken: ct, timeout: TimeSpan.FromMinutes(10));

            var sysUi = await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Set-SysUILang:ru-RU"],
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(10));
            log?.Invoke(sysUi.ExitCode == 0 ? "Preferred system UI language настроен." : "DISM не поддерживает /Set-SysUILang; продолжаю.");

            var intl = await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Get-Intl"], cancellationToken: ct);
            if (!intl.StandardOutput.Contains("ru-ru", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"DISM не подтвердил ru-RU для индекса {image.Index}.");

            var lxpResults = await new LocalExperiencePackService(_runner)
                .ProvisionAsync(mount, lxpDirectories, image.NormalizedArchitecture, ct, log);

            LocalizationStep? winReAudit = null;
            if (serviceWinRe is not null)
            {
                winReAudit = await serviceWinRe(mount);
                if (!allowPartialLocalization && winReAudit.Status is not ("localized" or "not_present"))
                    throw new InvalidOperationException($"WinRE не локализован полностью: {winReAudit.Status}. {winReAudit.Error}");
            }
            if (!string.IsNullOrWhiteSpace(unattendFile))
            {
                log?.Invoke($"Индекс {image.Index}: применяю unattend.xml.");
                await _runner.RunAsync("dism.exe",
                    [$"/Image:{mount}", $"/Apply-Unattend:{Path.GetFullPath(unattendFile)}"],
                    cancellationToken: ct, timeout: TimeSpan.FromMinutes(15));
            }
            await ApplyUpdatesAsync(mount, updates, "after_language", ct, log);
            await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
            mounted = false;

            await using var stream = File.OpenRead(selected);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            Audit.Add(new ImageAudit(Path.GetFileName(imageFile), image.Index, image.NormalizedArchitecture,
                image.Version, Path.GetFileName(selected), hash, true, capabilityResults, winReAudit, lxpResults));
            log?.Invoke($"Индекс {image.Index} локализован.");
        }
        catch
        {
            if (mounted) await DiscardAsync(mount);
            throw;
        }
        finally
        {
            try { if (Directory.Exists(mount) && !mounted) Directory.Delete(mount, true); } catch { }
        }
    }

    private async Task ApplyUpdatesAsync(string mount, UpdatePlan? plan, string phase, CancellationToken ct, Action<string>? log)
    {
        if (plan is null) return;
        var selected = phase == "before_language"
            ? plan.Packages.Where(x => x.Kind is "checkpoint" or "target_lcu")
            : plan.Packages.Where(x => x.Kind == "target_lcu");
        foreach (var update in selected)
        {
            log?.Invoke($"Интеграция {update.Kind}: {update.File}");
            await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{update.FullPath}", "/NoRestart"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
        }
    }

    private async Task DiscardAsync(string mount)
    {
        await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Discard"],
            cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(15));
    }

    private static string? Field(string output, string name)
    {
        var match = Regex.Match(output, @"(?im)^\s*" + Regex.Escape(name) + @"\s*:\s*(.+?)\s*$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    internal static bool IsLanguagePackForImage(string path, ImageInfo image)
    {
        var productFamily = PackageCompatibilityService.ProductFamily(image);
        return PackageCompatibilityService.IsServer(productFamily)
            ? IsServerLp(path, image.NormalizedArchitecture)
            : IsClientLp(path, image.NormalizedArchitecture);
    }

    internal static bool IsServerLp(string path, string arch)
    {
        var fileName = Path.GetFileName(path);
        var identity = fileName.Equals("update.mum", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(path) ?? "")
            : fileName;
        if (!fileName.Equals("update.mum", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(fileName).Equals(".cab", StringComparison.OrdinalIgnoreCase)) return false;
        var name = identity.ToLowerInvariant();
        if (!name.Contains("ru-ru", StringComparison.Ordinal) ||
            !(name.Contains("server-languagepack", StringComparison.Ordinal) ||
              name.Contains("server-language-pack", StringComparison.Ordinal) ||
              name.Contains("serverlanguagepack", StringComparison.Ordinal)) ||
            new[] { "winpe", "setup", "recovery", "languagefeatures", "languageexperiencepack" }.Any(name.Contains))
            return false;
        return ContainsArchitecture(name, arch);
    }

    private static bool ContainsArchitecture(string name, string architecture)
    {
        var arch = BuildNumbers.Architecture(architecture);
        return arch switch
        {
            "x64" => name.Contains("x64") || name.Contains("amd64") || name.Contains("x86_64"),
            "arm64" => name.Contains("arm64"),
            "x86" => (name.Contains("_x86_") || name.Contains("-x86-") || name.Contains("~x86~")) &&
                     !name.Contains("x86_64") && !name.Contains("x86-64"),
            _ => false
        };
    }

    internal static bool IsClientLp(string path, string arch)
    {
        var fileName = Path.GetFileName(path);
        var identity = fileName.Equals("update.mum", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(Path.GetDirectoryName(path) ?? "")
            : fileName;
        var suffix = Path.GetExtension(fileName);
        if (!fileName.Equals("update.mum", StringComparison.OrdinalIgnoreCase) &&
            !suffix.Equals(".cab", StringComparison.OrdinalIgnoreCase)) return false;
        return IsClientLpName(identity, arch);
    }

    internal static bool IsClientLpName(string value, string architecture)
    {
        var name = Path.GetFileName(value).ToLowerInvariant();
        if (!name.Contains("ru-ru", StringComparison.Ordinal) ||
            !new[] { "client-language-pack", "client-languagepack", "clientlanguagepack", "language-pack", "languagepack" }.Any(name.Contains) ||
            new[] { "server", "winpe", "setup", "recovery", "languagefeatures", "local-experience-pack", "languageexperiencepack" }.Any(name.Contains))
            return false;
        var arch = BuildNumbers.Architecture(architecture);
        return arch switch
        {
            "x64" => name.Contains("x64") || name.Contains("amd64") || name.Contains("x86_64"),
            "arm64" => name.Contains("arm64"),
            "x86" => (name.Contains("_x86_") || name.Contains("-x86-") || name.Contains("~x86~")) &&
                     !name.Contains("x86_64") && !name.Contains("x86-64"),
            _ => false
        };
    }
}
