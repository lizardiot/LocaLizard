using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed class WindowsImageService(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;
    public List<ImageAudit> Audit { get; } = [];

    public async Task<IReadOnlyList<ImageInfo>> GetImageInfoAsync(string file, CancellationToken ct)
    {
        var summary = await _runner.RunAsync("dism.exe", ["/Get-WimInfo", $"/WimFile:{file}", "/English"],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(3));
        var indexes = Regex.Matches(summary.StandardOutput, @"(?im)^\s*Index\s*:\s*(\d+)\s*$")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToArray();
        var result = new List<ImageInfo>();
        foreach (var index in indexes)
        {
            var detail = await _runner.RunAsync("dism.exe", ["/Get-WimInfo", $"/WimFile:{file}", $"/Index:{index}", "/English"],
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
        string? fod, UpdatePlan? updates, string work, Action<string>? log, CancellationToken ct)
    {
        if (image.BuildFamily == 0 || !BuildNumbers.IsSupportedArchitecture(image.NormalizedArchitecture))
            throw new InvalidDataException($"Не удалось определить сборку/архитектуру индекса {image.Index}.");
        if (updates is not null && (updates.TargetBuildFamily != image.BuildFamily || updates.Architecture != image.NormalizedArchitecture))
            throw new InvalidDataException("Набор обновлений не соответствует сборке/архитектуре индекса.");

        var candidates = packageCandidates.Where(p => IsClientLp(p, image.NormalizedArchitecture))
            .OrderBy(p => Path.GetFileName(p).Equals("update.mum", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
        if (candidates.Length == 0) throw new FileNotFoundException($"Не найден Client Language Pack ru-RU для {image.NormalizedArchitecture}.");

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

            await ApplyUpdatesAsync(mount, updates, "before_language", ct, log);
            var caps = new[]
            {
                "Language.Basic~~~ru-RU~0.0.1.0",
                "Language.OCR~~~ru-RU~0.0.1.0",
                "Language.TextToSpeech~~~ru-RU~0.0.1.0",
                "Language.Handwriting~~~ru-RU~0.0.1.0"
            };
            if (string.IsNullOrWhiteSpace(fod))
                capabilityResults.AddRange(caps.Select(x => new CapabilityAudit(x, "not_requested")));
            else if (!Directory.Exists(fod))
                capabilityResults.AddRange(caps.Select(x => new CapabilityAudit(x, "source_missing")));
            else
            {
                foreach (var capability in caps)
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await _runner.RunAsync("dism.exe",
                        [$"/Image:{mount}", "/Add-Capability", $"/CapabilityName:{capability}", $"/Source:{fod}", "/LimitAccess"],
                        cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(30));
                    capabilityResults.Add(new CapabilityAudit(capability, result.ExitCode == 0 ? "installed" : "unavailable", result.ExitCode));
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

            await ApplyUpdatesAsync(mount, updates, "after_language", ct, log);
            await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
            mounted = false;

            await using var stream = File.OpenRead(selected);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            Audit.Add(new ImageAudit(Path.GetFileName(imageFile), image.Index, image.NormalizedArchitecture,
                image.Version, Path.GetFileName(selected), hash, true, capabilityResults));
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

    private static bool IsClientLp(string path, string arch)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name == "update.mum")
            return (Path.GetDirectoryName(path) ?? "").Contains("client-languagepack", StringComparison.OrdinalIgnoreCase);
        if (!name.EndsWith(".cab") || !name.Contains("ru-ru") ||
            new[] { "server", "winpe", "setup", "recovery", "languagefeatures", "local-experience-pack" }.Any(name.Contains))
            return false;
        if (!new[] { "client-language-pack", "client-languagepack", "clientlanguagepack", "language-pack", "languagepack" }.Any(name.Contains))
            return false;
        return arch switch
        {
            "x64" => name.Contains("x64") || name.Contains("amd64") || name.Contains("x86_64"),
            "arm64" => name.Contains("arm64"),
            "x86" => Regex.IsMatch(name, @"(^|[-_.])x86([-_.]|$)") && !name.Contains("x86_64"),
            _ => false
        };
    }
}
