using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record SplitWimAudit(string SourceWim, string FirstPart, IReadOnlyList<string> Parts, int Indexes, int PartSizeMb);
public sealed record LocalizationStep(string Status, string? Source = null, IReadOnlyList<string>? Packages = null, string? Error = null);
public sealed record HyperVTestResult(string Status, bool VisualSetupConfirmed, string? VmName = null, string? Details = null);

public sealed record AppSettings
{
    [JsonPropertyName("source_iso")] public string SourceIso { get; init; } = "";
    [JsonPropertyName("donor_iso")] public string DonorIso { get; init; } = "";
    [JsonPropertyName("output_iso")] public string OutputIso { get; init; } = "";
    [JsonPropertyName("work_root")] public string WorkRoot { get; init; } = "";
    [JsonPropertyName("package_root")] public string PackageRoot { get; init; } = "";
    [JsonPropertyName("fod_source")] public string FodSource { get; init; } = "";
    [JsonPropertyName("updates_root")] public string UpdatesRoot { get; init; } = "";
    [JsonPropertyName("keep_work")] public bool KeepWorkFiles { get; init; } = true;
    [JsonPropertyName("process_setup")] public bool ProcessSetup { get; init; } = true;
    [JsonPropertyName("split_wim")] public bool SplitWim { get; init; }
    [JsonPropertyName("vm_test")] public bool RunHyperVTest { get; init; }
    [JsonPropertyName("driver_directory")] public string DriverDirectory { get; init; } = "";
    [JsonPropertyName("unattend_file")] public string UnattendFile { get; init; } = "";
    [JsonPropertyName("allow_partial_localization")] public bool AllowPartialLocalization { get; init; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var folder = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(folder);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }
}

public sealed class WimSplitService(ProcessRunner runner, WindowsImageService images)
{
    private readonly ProcessRunner _runner = runner;
    private readonly WindowsImageService _images = images;

    public static IReadOnlyList<string> Sequence(string folder, string stem = "install")
    {
        var first = Path.Combine(folder, stem + ".swm");
        if (!File.Exists(first)) return [];
        var numbered = new SortedDictionary<int, string>();
        foreach (var path in Directory.EnumerateFiles(folder, stem + "*.swm"))
        {
            var name = Path.GetFileName(path);
            if (name.Equals(stem + ".swm", StringComparison.OrdinalIgnoreCase)) continue;
            var match = Regex.Match(name, "^" + Regex.Escape(stem) + "([0-9]+)\\.swm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var index = int.Parse(match.Groups[1].Value);
            if (index < 2 || !numbered.TryAdd(index, path))
                throw new InvalidDataException($"Некорректная/повторная часть split-WIM: {name}");
        }
        var maximum = numbered.Count == 0 ? 1 : numbered.Keys.Max();
        var missing = Enumerable.Range(2, Math.Max(0, maximum - 1)).Where(x => !numbered.ContainsKey(x)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"Набор {stem}.swm неполный: отсутствуют части {string.Join(", ", missing)}.");
        return new[] { first }.Concat(numbered.Values).ToArray();
    }

    public async Task<IReadOnlyList<string>> MergeInputAsync(string sourcesDirectory, CancellationToken ct, Action<string>? log = null)
    {
        var stems = Directory.EnumerateFiles(sourcesDirectory, "install*.swm")
            .Select(p => Regex.Match(Path.GetFileName(p), "^(install.*?)\\.swm$", RegexOptions.IgnoreCase))
            .Where(m => m.Success).Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
        var merged = new List<string>();
        foreach (var stem in stems)
        {
            ct.ThrowIfCancellationRequested();
            var parts = Sequence(sourcesDirectory, stem);
            if (parts.Count == 0) continue;
            var target = Path.Combine(sourcesDirectory, stem + ".wim");
            if (File.Exists(target))
                throw new InvalidDataException($"Найден одновременно {Path.GetFileName(target)} и split-SWM; источник неоднозначен.");
            var pattern = Path.Combine(sourcesDirectory, stem + "*.swm");
            log?.Invoke($"Объединяю split-WIM {Path.GetFileName(parts[0])}: частей {parts.Count}.");
            var sourceInfos = await _images.GetImageInfoAsync(parts[0], ct, pattern);
            if (sourceInfos.Count == 0) throw new InvalidDataException($"В наборе {stem}.swm нет читаемых WIM-индексов.");
            foreach (var info in sourceInfos)
            {
                ct.ThrowIfCancellationRequested();
                await _runner.RunAsync("dism.exe",
                    ["/Export-Image", $"/SourceImageFile:{parts[0]}", $"/SWMFile:{pattern}",
                     $"/SourceIndex:{info.Index}", $"/DestinationImageFile:{target}",
                     $"/DestinationName:{info.Name}", "/Compress:max", "/CheckIntegrity"],
                    cancellationToken: ct, timeout: TimeSpan.FromHours(3));
            }
            var outputInfos = await _images.GetImageInfoAsync(target, ct);
            if (outputInfos.Count != sourceInfos.Count)
            {
                try { File.Delete(target); } catch { }
                throw new InvalidDataException($"Объединение split-WIM {stem} неполное: было {sourceInfos.Count}, стало {outputInfos.Count} индексов.");
            }
            foreach (var part in parts) File.Delete(part);
            merged.Add(target);
            log?.Invoke($"Split-WIM объединён в {Path.GetFileName(target)}; проверено индексов: {outputInfos.Count}.");
        }
        return merged;
    }

    public async Task<IReadOnlyList<SplitWimAudit>> SplitOutputAsync(string sourcesDirectory, bool enabled, CancellationToken ct, Action<string>? log = null)
    {
        if (!enabled) return [];
        var reports = new List<SplitWimAudit>();
        foreach (var wim in Directory.EnumerateFiles(sourcesDirectory, "install*.wim").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var stem = Path.GetFileNameWithoutExtension(wim);
            var first = Path.ChangeExtension(wim, ".swm");
            log?.Invoke($"Разделяю {Path.GetFileName(wim)} на SWM по 3800 MiB.");
            await _runner.RunAsync("dism.exe",
                ["/Split-Image", $"/ImageFile:{wim}", $"/SWMFile:{first}", "/FileSize:3800", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromHours(2));
            var parts = Sequence(sourcesDirectory, stem);
            if (parts.Count == 0 || parts.Any(p => new FileInfo(p).Length == 0))
                throw new InvalidDataException($"DISM не создал полный split-WIM набор для {Path.GetFileName(wim)}.");
            if (parts.Any(p => new FileInfo(p).Length > 4L * 1024 * 1024 * 1024))
                throw new InvalidDataException($"Одна из SWM-частей {stem} больше 4 GiB.");
            var original = await _images.GetImageInfoAsync(wim, ct);
            var split = await _images.GetImageInfoAsync(first, ct, Path.Combine(sourcesDirectory, stem + "*.swm"));
            if (split.Count != original.Count)
                throw new InvalidDataException($"Проверка SWM {stem} не прошла: {original.Count} индексов в WIM и {split.Count} в SWM.");
            reports.Add(new SplitWimAudit(Path.GetFileName(wim), Path.GetFileName(first),
                parts.Select(Path.GetFileName).ToArray()!, split.Count, 3800));
            File.Delete(wim);
        }
        return reports;
    }
}

public sealed class AdkBootstrapper(ProcessRunner runner)
{
    private const string AdkSetupUrl = "https://go.microsoft.com/fwlink/?linkid=2289980";
    private const string WinPeSetupUrl = "https://go.microsoft.com/fwlink/?linkid=2289981";
    private readonly ProcessRunner _runner = runner;
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard", "ADK");

    public static string? FindOscdimg()
    {
        var roots = new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Root
        }.Where(x => !string.IsNullOrWhiteSpace(x) && Directory.Exists(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var paths = roots.SelectMany(root => EnumerateFilesSafely(root, "oscdimg.exe"))
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Where(Directory.Exists).Select(p => Path.Combine(p, "oscdimg.exe")).Where(File.Exists))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<(string Path, Version Version)>();
        foreach (var path in paths)
        {
            try
            {
                candidates.Add((path, ReadFileVersion(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // A stale or protected candidate must not prevent checking the remaining locations.
            }
        }

        return candidates.OrderByDescending(item => item.Version)
            .Select(item => item.Path).FirstOrDefault();
    }

    internal static IEnumerable<string> EnumerateFilesSafely(
        string root,
        string searchPattern,
        Func<string, IEnumerable<string>>? enumerateDirectories = null,
        Func<string, string, IEnumerable<string>>? enumerateFiles = null)
    {
        if (string.IsNullOrWhiteSpace(root)) yield break;
        enumerateDirectories ??= Directory.EnumerateDirectories;
        enumerateFiles ??= Directory.EnumerateFiles;

        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string fullPath;
            try { fullPath = Path.GetFullPath(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            if (!visited.Add(fullPath)) continue;

            // Enumerate each folder independently. In particular, do not use
            // SearchOption.AllDirectories: its lazy iterator can throw from MoveNext()
            // after entering a protected child and abort the entire ADK search.
            foreach (var file in EnumerateSafely(() => enumerateFiles(directory, searchPattern)))
                yield return file;

            foreach (var child in EnumerateSafely(() => enumerateDirectories(directory)))
                pending.Push(child);
        }
    }

    private static IEnumerable<string> EnumerateSafely(Func<IEnumerable<string>> createSequence)
    {
        IEnumerator<string>? enumerator = null;
        try { enumerator = createSequence().GetEnumerator(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (enumerator is null) yield break;

        try
        {
            while (true)
            {
                var moved = false;
                var failed = false;
                string? current = null;
                try
                {
                    moved = enumerator.MoveNext();
                    if (moved) current = enumerator.Current;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed = true;
                }

                if (failed || !moved) yield break;
                yield return current!;
            }
        }
        finally
        {
            try { enumerator.Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static Version ReadFileVersion(string path)
    {
        var raw = FileVersionInfo.GetVersionInfo(path).FileVersion;
        return Version.TryParse(raw, out var parsed) ? parsed : new Version(0, 0);
    }

    public async Task<string> EnsureOscdimgAsync(CancellationToken ct, Action<string>? log = null)
    {
        var existing = FindOscdimg();
        if (existing is not null) return existing;
        EnsureWindowsAdmin();
        Directory.CreateDirectory(Root);
        var installer = await DownloadMicrosoftBootstrapperAsync(AdkSetupUrl, "adksetup.exe", ct, log);
        await VerifyMicrosoftSignatureAsync(installer, ct);
        log?.Invoke("Устанавливаю Windows ADK Deployment Tools в каталог LocaLizard.");
        await _runner.RunAsync(installer,
            ["/quiet", "/norestart", "/installpath", Root, "/features", "OptionId.DeploymentTools"],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(40));
        return FindOscdimg() ?? throw new FileNotFoundException("ADK установлен, но oscdimg.exe не найден.");
    }

    public async Task<string?> EnsureWinPeAddonAsync(string architecture, CancellationToken ct, Action<string>? log = null)
    {
        var found = FindWinPeLanguageDirectory(architecture);
        if (found is not null) return found;
        EnsureWindowsAdmin();
        Directory.CreateDirectory(Root);
        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        if (drive.AvailableFreeSpace < 5L * 1024 * 1024 * 1024)
        {
            log?.Invoke("Для WinPE Add-on требуется ориентировочно 5 GiB свободного места; автоматическая установка пропущена.");
            return null;
        }
        var installer = await DownloadMicrosoftBootstrapperAsync(WinPeSetupUrl, "adkwinpesetup.exe", ct, log);
        await VerifyMicrosoftSignatureAsync(installer, ct);
        log?.Invoke("Устанавливаю официальный WinPE Add-on Microsoft.");
        await _runner.RunAsync(installer,
            ["/quiet", "/norestart", "/ceip", "off", "/installpath", Root,
             "/features", "OptionId.WindowsPreinstallationEnvironment"],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(45));
        return FindWinPeLanguageDirectory(architecture);
    }

    public static string? FindWinPeLanguageDirectory(string architecture)
    {
        var arch = BuildNumbers.Architecture(architecture);
        var folderArch = arch switch { "x64" => "amd64", "x86" => "x86", "arm64" => "arm64", _ => "" };
        if (folderArch.Length == 0) return null;
        var roots = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Assessment and Deployment Kit", "Windows Preinstallation Environment"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Kits", "10", "Assessment and Deployment Kit", "Windows Preinstallation Environment"),
            Path.Combine(Root, "Windows Preinstallation Environment")
        };
        foreach (var root in roots)
        {
            var direct = Path.Combine(root, folderArch, "WinPE_OCs", "ru-ru");
            if (File.Exists(Path.Combine(direct, "lp.cab"))) return direct;
        }
        return null;
    }

    private static async Task<string> DownloadMicrosoftBootstrapperAsync(string url, string name, CancellationToken ct, Action<string>? log)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard", "download");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, name);
        log?.Invoke($"Загружаю официальный установщик Microsoft: {name}.");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(15) };
        var current = new Uri(url);
        HttpResponseMessage? finalResponse = null;
        for (var redirects = 0; redirects <= 6; redirects++)
        {
            if (current.Scheme != Uri.UriSchemeHttps || !IsMicrosoftHost(current.Host) || !string.IsNullOrEmpty(current.UserInfo))
                throw new InvalidDataException($"Недоверенный адрес установщика ADK: {current}.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location
                    ?? throw new InvalidDataException("Перенаправление установщика ADK не содержит Location.");
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                current = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            finalResponse = response;
            break;
        }
        if (finalResponse is null)
            throw new InvalidDataException("Превышено число перенаправлений при загрузке ADK.");
        var temp = target + ".download";
        using (finalResponse)
        {
            try
            {
                await using var source = await finalResponse.Content.ReadAsStreamAsync(ct);
                await using var destination = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
                await source.CopyToAsync(destination, ct);
                await destination.FlushAsync(ct);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }
        if (new FileInfo(temp).Length < 100_000)
        {
            File.Delete(temp);
            throw new InvalidDataException($"Загруженный установщик {name} слишком мал.");
        }
        File.Move(temp, target, true);
        return target;
    }

    private async Task VerifyMicrosoftSignatureAsync(string installer, CancellationToken ct)
    {
        var path = installer.Replace("'", "''");
        var script = "$s=Get-AuthenticodeSignature -LiteralPath '" + path + "'; " +
                     "if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'Microsoft') { exit 81 }; " +
                     "Write-Output 'AUTHENTICODE_VALID'";
        var result = await _runner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken: ct, timeout: TimeSpan.FromSeconds(60));
        if (!result.StandardOutput.Contains("AUTHENTICODE_VALID", StringComparison.Ordinal))
            throw new InvalidDataException("Установщик ADK не прошёл проверку Authenticode Microsoft; запуск запрещён.");
    }

    private static bool IsMicrosoftHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        return host == "microsoft.com" || host.EndsWith(".microsoft.com", StringComparison.Ordinal) ||
               host == "windows.com" || host.EndsWith(".windows.com", StringComparison.Ordinal) ||
               host == "microsoftonline.com" || host.EndsWith(".microsoftonline.com", StringComparison.Ordinal);
    }

    private static void EnsureWindowsAdmin()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Установка ADK доступна только в Windows.");
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Установка ADK требует прав администратора.");
    }
}

public sealed class WinReService(ProcessRunner runner, AdkBootstrapper adk)
{
    private readonly ProcessRunner _runner = runner;
    private readonly AdkBootstrapper _adk = adk;

    public async Task<LocalizationStep> LocalizeAsync(
        string windowsMount, string architecture, string? fodRoot, UpdatePlan? updates,
        CancellationToken ct, Action<string>? log = null)
    {
        var winre = Path.Combine(windowsMount, "Windows", "System32", "Recovery", "winre.wim");
        if (!File.Exists(winre)) return new LocalizationStep("not_present");
        var arch = BuildNumbers.Architecture(architecture);
        var kitArch = arch switch { "x64" => "amd64", "x86" => "x86", "arm64" => "arm64", _ => "" };
        if (kitArch.Length == 0) return new LocalizationStep("unsupported_architecture");
        var peDir = FindFodWinPeDirectory(fodRoot, kitArch);
        if (peDir is null)
        {
            log?.Invoke("В FOD источнике нет WinPE_OCs ru-RU; проверяю официальный WinPE Add-on.");
            try { peDir = await _adk.EnsureWinPeAddonAsync(arch, ct, log); }
            catch (Exception ex) { log?.Invoke("WinPE Add-on недоступен: " + ex.Message); }
        }
        if (peDir is null || !File.Exists(Path.Combine(peDir, "lp.cab")))
            return new LocalizationStep("language_resources_missing", peDir);
        var mount = Path.Combine(Path.GetTempPath(), "LocaLizard_WinRE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        var mounted = false;
        var applied = new List<string>();
        try
        {
            await _runner.RunAsync("dism.exe", ["/Mount-Image", $"/ImageFile:{winre}", "/Index:1", $"/MountDir:{mount}", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(30));
            mounted = true;
            await ApplyUpdatePhaseAsync(mount, updates, "winre", ct, log);
            var inventory = await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Get-Packages", "/Format:Table"],
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(10));
            var baseLp = Path.Combine(peDir, "lp.cab");
            await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{baseLp}", "/NoRestart"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
            applied.Add(Path.GetFileName(baseLp));
            foreach (var package in Directory.EnumerateFiles(peDir, "*_ru-ru.cab").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var low = Path.GetFileName(package).ToLowerInvariant();
                if (low == "lp.cab" || low.Contains("winpe-wifi")) continue;
                var component = low.Replace("_ru-ru.cab", "");
                if (!string.IsNullOrEmpty(component) && inventory.StandardOutput.Contains(component, StringComparison.OrdinalIgnoreCase))
                {
                    var result = await _runner.RunAsync("dism.exe",
                        [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{package}", "/NoRestart"],
                        cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromMinutes(45));
                    if (result.ExitCode == 0) applied.Add(Path.GetFileName(package));
                    else log?.Invoke($"Необязательный WinRE компонент {Path.GetFileName(package)} не добавлен (код {result.ExitCode}).");
                }
            }
            await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
            mounted = false;
            log?.Invoke($"WinRE локализован: {applied.Count} CAB-пакетов.");
            return new LocalizationStep("localized", peDir, applied);
        }
        catch (Exception ex)
        {
            if (mounted)
                await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Discard"],
                    cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(20));
            if (ex is OperationCanceledException) throw;
            log?.Invoke("WinRE не удалось локализовать; вложенный образ будет откачен: " + ex.Message);
            return new LocalizationStep("failed", peDir, applied, ex.Message);
        }
        finally
        {
            try { if (Directory.Exists(mount)) Directory.Delete(mount, true); } catch { }
        }
    }

    private static string? FindFodWinPeDirectory(string? fod, string kitArch)
    {
        if (string.IsNullOrWhiteSpace(fod) || !Directory.Exists(fod)) return null;
        var candidates = new[]
        {
            Path.Combine(fod, "Windows Preinstallation Environment", kitArch, "WinPE_OCs", "ru-ru"),
            Path.Combine(fod, "LanguagesAndOptionalFeatures", "Windows Preinstallation Environment", kitArch, "WinPE_OCs", "ru-ru")
        };
        foreach (var candidate in candidates)
            if (File.Exists(Path.Combine(candidate, "lp.cab"))) return candidate;
        try
        {
            return Directory.EnumerateFiles(fod, "lp.cab", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName).FirstOrDefault(p => p is not null &&
                    Path.GetFileName(p).Equals("ru-ru", StringComparison.OrdinalIgnoreCase) &&
                    p.Contains("WinPE_OCs", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task ApplyUpdatePhaseAsync(string mount, UpdatePlan? plan, string phase,
        CancellationToken ct, Action<string>? log = null)
    {
        if (plan is null) return;
        IEnumerable<UpdatePackage> selected = phase switch
        {
            "before_language" => plan.Packages.Where(x => x.Kind is "checkpoint" or "target_lcu"),
            "after_language" => plan.Packages.Where(x => x.Kind == "target_lcu"),
            "winre" => plan.Packages.Where(x => x.Kind is "winre_ssu" or "safeos_dynamic_update"),
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            log?.Invoke($"{phase}: {item.Kind} {item.File}");
            await new ProcessRunner().RunAsync("dism.exe",
                [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{item.FullPath}", "/NoRestart"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
        }
    }
}

public sealed class SetupLocalizationService(ProcessRunner runner, WindowsImageService images, AdkBootstrapper adk)
{
    private readonly ProcessRunner _runner = runner;
    private readonly WindowsImageService _images = images;
    private readonly AdkBootstrapper _adk = adk;

    public async Task<LocalizationStep> LocalizeAsync(string media, string architecture, string? fodRoot,
        IEnumerable<string>? donorSetupPackages, UpdatePlan? updates, CancellationToken ct, Action<string>? log = null)
    {
        var boot = Path.Combine(media, "sources", "boot.wim");
        var install = Path.Combine(media, "sources", "install.wim");
        if (!File.Exists(boot) || !File.Exists(install)) return new LocalizationStep("boot_or_install_missing");
        string? peDir = null;
        try { peDir = await _adk.EnsureWinPeAddonAsync(architecture, ct, log); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Invoke("Официальный WinPE Add-on недоступен; использую CAB из ISO-донора: " + ex.Message);
        }
        var donor = (donorSetupPackages ?? []).Where(File.Exists)
            .Select(path => (Name: Path.GetFileName(path), Path: path))
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => item.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
        string? Package(string name)
        {
            var local = peDir is null ? null : Path.Combine(peDir, name);
            if (local is not null && File.Exists(local)) return local;
            return donor.TryGetValue(name, out var found) ? found : null;
        }
        var baseLp = Package("lp.cab");
        var setup = Package("WinPE-Setup_ru-ru.cab");
        var client = Package("WinPE-Setup-Client_ru-ru.cab");
        if (baseLp is null || setup is null || client is null)
        {
            log?.Invoke("Для Setup необходимы lp.cab, WinPE-Setup_ru-ru.cab и WinPE-Setup-Client_ru-ru.cab.");
            return new LocalizationStep("required_setup_packages_missing", peDir);
        }

        var localized = new List<string>();
        foreach (var index in new[] { 2, 1 })
        {
            ct.ThrowIfCancellationRequested();
            var info = (await _images.GetImageInfoAsync(boot, ct)).FirstOrDefault(x => x.Index == index);
            if (info is null) { log?.Invoke($"В boot.wim отсутствует индекс {index}."); continue; }
            var mount = Path.Combine(Path.GetTempPath(), $"LocaLizard_Setup_{index}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(mount);
            var mounted = false;
            try
            {
                await _runner.RunAsync("dism.exe", ["/Mount-Image", $"/ImageFile:{boot}", $"/Index:{index}", $"/MountDir:{mount}", "/CheckIntegrity"],
                    cancellationToken: ct, timeout: TimeSpan.FromMinutes(30));
                mounted = true;
                await WinReService.ApplyUpdatePhaseAsync(mount, updates, "before_language", ct, log);
                var inventory = await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Get-Packages", "/Format:Table"],
                    cancellationToken: ct, throwOnError: false);
                var required = new List<string> { baseLp };
                if (index == 2) required.AddRange([setup, client]);
                else
                {
                    foreach (var candidate in (peDir is null ? [] : Directory.EnumerateFiles(peDir, "*_ru-ru.cab"))
                                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var file = Path.GetFileName(candidate);
                        if (file.Equals("lp.cab", StringComparison.OrdinalIgnoreCase) ||
                            file.StartsWith("WinPE-Setup", StringComparison.OrdinalIgnoreCase)) continue;
                        var component = file[..^"_ru-ru.cab".Length];
                        if (inventory.StandardOutput.Contains(component, StringComparison.OrdinalIgnoreCase)) required.Add(candidate);
                    }
                }
                foreach (var package in required.Distinct(StringComparer.OrdinalIgnoreCase))
                    await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Add-Package", $"/PackagePath:{package}", "/NoRestart"],
                        cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
                await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Set-AllIntl:ru-RU"],
                    cancellationToken: ct, throwOnError: false);
                await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Set-UILang:ru-RU"],
                    cancellationToken: ct, throwOnError: false);
                if (index == 2)
                    await _runner.RunAsync("dism.exe", [$"/Image:{mount}", "/Set-SetupUILang:ru-RU", $"/Distribution:{media}"],
                        cancellationToken: ct, throwOnError: false);
                await WinReService.ApplyUpdatePhaseAsync(mount, updates, "after_language", ct, log);
                await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity"],
                    cancellationToken: ct, timeout: TimeSpan.FromMinutes(60));
                mounted = false;
                localized.Add($"index{index}:{string.Join(",", required.Select(Path.GetFileName))}");
                log?.Invoke($"boot.wim index {index}: добавлено RU CAB — {required.Count}.");
            }
            catch (Exception ex)
            {
                if (mounted)
                    await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Discard"],
                        cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(20));
                if (ex is OperationCanceledException || index == 2) throw;
                log?.Invoke("WinPE index 1 пропущен после ошибки; Setup index 2 остаётся обработанным: " + ex.Message);
            }
            finally { try { if (Directory.Exists(mount)) Directory.Delete(mount, true); } catch { } }
        }

        var genMount = Path.Combine(Path.GetTempPath(), "LocaLizard_GenLang_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(genMount);
        var genMounted = false;
        try
        {
            await _runner.RunAsync("dism.exe", ["/Mount-Image", $"/ImageFile:{install}", "/Index:1", $"/MountDir:{genMount}", "/CheckIntegrity"],
                cancellationToken: ct, timeout: TimeSpan.FromMinutes(30));
            genMounted = true;
            var gen = await _runner.RunAsync("dism.exe", [$"/Image:{genMount}", "/Gen-LangINI", $"/Distribution:{media}"],
                cancellationToken: ct, throwOnError: false);
            if (gen.ExitCode != 0) log?.Invoke("DISM не смог автоматически создать lang.ini.");
        }
        finally
        {
            if (genMounted)
                await _runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{genMount}", "/Discard"],
                    cancellationToken: CancellationToken.None, throwOnError: false);
            try { if (Directory.Exists(genMount)) Directory.Delete(genMount, true); } catch { }
        }
        var langIni = Path.Combine(media, "sources", "lang.ini");
        var setupIndexDone = localized.Any(x => x.StartsWith("index2:", StringComparison.Ordinal));
        var winPeIndexDone = localized.Any(x => x.StartsWith("index1:", StringComparison.Ordinal));
        var setupStatus = setupIndexDone && winPeIndexDone ? "localized" : setupIndexDone ? "partial" : "failed";
        return new LocalizationStep(setupStatus, peDir, localized,
            setupStatus == "localized" ? null : "Не все обязательные индексы boot.wim получили RU-компоненты.");
    }
}

public sealed class HyperVSmokeTestService(ProcessRunner runner)
{
    private readonly ProcessRunner _runner = runner;

    public async Task<HyperVTestResult> RunAsync(string isoPath, string workDirectory,
        Func<string, Task<bool>> confirmVisibleSetup, CancellationToken ct, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) return new HyperVTestResult("unavailable", false, Details: "Windows is required.");
        if (!File.Exists(isoPath)) return new HyperVTestResult("unavailable", false, Details: "ISO missing.");
        using (var identity = WindowsIdentity.GetCurrent())
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return new HyperVTestResult("unavailable", false, Details: "Administrator privileges are required.");
        var token = Guid.NewGuid().ToString("N")[..8];
        var vmName = "LocaLizard-Smoke-" + token;
        var root = Path.Combine(workDirectory, "hyperv_smoke_" + token);
        var script = Path.Combine(workDirectory, "hyperv_" + token + ".ps1");
        Directory.CreateDirectory(workDirectory);
        var qVm = vmName.Replace("'", "''");
        var qRoot = root.Replace("'", "''");
        var qIso = Path.GetFullPath(isoPath).Replace("'", "''");
        var create = "$ErrorActionPreference='Stop'; $n='" + qVm + "'; $r='" + qRoot + "'; $i='" + qIso + "'; " +
                     "if (-not (Get-Command New-VM -ErrorAction SilentlyContinue)) { throw 'Hyper-V module unavailable' }; " +
                     "New-Item -ItemType Directory -Force -Path $r | Out-Null; " +
                     "New-VM -Name $n -Generation 2 -MemoryStartupBytes 2GB -NoVHD -Path $r | Out-Null; " +
                     "Set-VMProcessor -VMName $n -Count 2; Get-VMNetworkAdapter -VMName $n | Remove-VMNetworkAdapter; " +
                     "Add-VMDvdDrive -VMName $n -Path $i | Out-Null; " +
                     "Set-VMFirmware -VMName $n -EnableSecureBoot Off -FirstBootDevice (Get-VMDvdDrive -VMName $n); " +
                     "Start-VM -Name $n | Out-Null; " +
                     "Start-Process -FilePath (Join-Path $env:WINDIR 'System32\\vmconnect.exe') -ArgumentList @('localhost',$n) -ErrorAction SilentlyContinue; " +
                     "Start-Sleep -Seconds 25; if ((Get-VM -Name $n).State.ToString() -ne 'Running') { throw 'VM stopped unexpectedly' }; " +
                     "Write-Output 'HYPERV_VM_STARTED'";
        try
        {
            await File.WriteAllTextAsync(script, create, ct);
            var result = await _runner.RunAsync("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script],
                cancellationToken: ct, timeout: TimeSpan.FromSeconds(90));
            if (!result.StandardOutput.Contains("HYPERV_VM_STARTED", StringComparison.Ordinal))
                return new HyperVTestResult("failed_or_unavailable", false, vmName, result.StandardOutput + result.StandardError);
            log?.Invoke("Hyper-V VM запущена без сетевого адаптера. Подтвердите, что в VMConnect отображается Windows Setup.");
            var confirmed = await confirmVisibleSetup(vmName);
            return new HyperVTestResult(confirmed ? "visual_setup_confirmed" : "visual_setup_not_confirmed", confirmed, vmName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new HyperVTestResult("unavailable", false, vmName, ex.Message);
        }
        finally
        {
            var cleanup = "$n='" + qVm + "'; $r='" + qRoot + "'; " +
                          "if (Get-VM -Name $n -ErrorAction SilentlyContinue) { Stop-VM -Name $n -TurnOff -Force -ErrorAction SilentlyContinue; Remove-VM -Name $n -Force -ErrorAction SilentlyContinue }; " +
                          "if (Test-Path -LiteralPath $r) { Remove-Item -LiteralPath $r -Recurse -Force -ErrorAction SilentlyContinue }";
            try
            {
                await _runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", cleanup],
                    cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(2));
            }
            catch (Exception ex) { log?.Invoke("Проверьте очистку временной Hyper-V VM вручную: " + ex.Message); }
            try { File.Delete(script); } catch { }
        }
    }
}
