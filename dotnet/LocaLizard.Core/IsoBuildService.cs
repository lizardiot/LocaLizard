using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed class IsoBuildService
{
    private readonly ProcessRunner _runner = new();
    private readonly WindowsImageService _images;
    private readonly UupDumpClient _uup = new();
    private readonly AdkBootstrapper _adk;
    private readonly WimSplitService _splitter;
    private readonly WinReService _winre;
    private readonly SetupLocalizationService _setup;
    private readonly HyperVSmokeTestService _hyperv;
    private readonly List<string> _donorSetupPackages = [];
    public Func<string, Task<bool>>? ConfirmHyperVSetup { get; set; }

    public event Action<string>? Log;
    public event Action<string>? Status;

    public IsoBuildService()
    {
        _images = new WindowsImageService(_runner);
        _adk = new AdkBootstrapper(_runner);
        _splitter = new WimSplitService(_runner, _images);
        _winre = new WinReService(_runner, _adk);
        _setup = new SetupLocalizationService(_runner, _images, _adk);
        _hyperv = new HyperVSmokeTestService(_runner);
        _runner.OutputLine += line => Log?.Invoke(line);
    }

    public async Task<string> RunAsync(RunOptions options, CancellationToken ct)
    {
        Validate(options);
        EnsureWindowsAdmin();
        var source = Path.GetFullPath(options.SourceIso);
        var output = Path.GetFullPath(options.OutputIso);
        var workRoot = Path.GetFullPath(options.WorkDirectory);
        var packageRoot = Path.GetFullPath(options.PackageDirectory);
        Directory.CreateDirectory(workRoot);
        Directory.CreateDirectory(packageRoot);

        var required = new FileInfo(source).Length * 3.5 + 8L * 1024 * 1024 * 1024;
        var free = new DriveInfo(Path.GetPathRoot(workRoot)!).AvailableFreeSpace;
        if (free < required)
            throw new IOException($"Недостаточно места: свободно {free:N0}, ориентировочно требуется {required:N0} байт.");

        var work = Path.Combine(workRoot, "LocaLizard_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        var media = Path.Combine(work, "media");
        Directory.CreateDirectory(media);
        var sourceMounted = false;
        var donorMounted = false;
        try
        {
            Status?.Invoke("Подключаю исходный ISO…");
            var sourceRoot = await MountIsoAsync(source, ct);
            sourceMounted = true;
            var copy = await _runner.RunAsync("robocopy.exe",
                [sourceRoot, media, "/E", "/COPY:DAT", "/DCOPY:DAT", "/A-:R", "/R:1", "/W:1", "/XJ", "/NFL", "/NDL", "/NP"],
                cancellationToken: ct, throwOnError: false, timeout: TimeSpan.FromHours(2));
            if (copy.ExitCode >= 8) throw new IOException($"Robocopy завершился с кодом {copy.ExitCode}.");
            await DismountIsoAsync(source);
            sourceMounted = false;

            var sources = Path.Combine(media, "sources");
            var boot = Path.Combine(sources, "boot.wim");
            if (!File.Exists(boot)) throw new InvalidDataException("В исходном ISO отсутствует sources\\boot.wim.");
            _donorSetupPackages.Clear();
            _ = await _splitter.MergeInputAsync(sources, ct, message => Log?.Invoke(message));
            var installImages = Directory.EnumerateFiles(sources, "install*.wim").ToList();
            var esdImages = Directory.EnumerateFiles(sources, "install*.esd").ToArray();
            if (installImages.Count == 0 && esdImages.Length == 0)
                throw new InvalidDataException("В исходном ISO не найден install.wim/install.esd.");

            foreach (var esd in esdImages)
            {
                ct.ThrowIfCancellationRequested();
                var wim = Path.ChangeExtension(esd, ".wim");
                Status?.Invoke($"Преобразую {Path.GetFileName(esd)} → WIM…");
                await _runner.RunAsync("dism.exe",
                    ["/Export-Image", $"/SourceImageFile:{esd}", "/All", $"/DestinationImageFile:{wim}", "/Compress:max", "/CheckIntegrity"],
                    cancellationToken: ct, timeout: TimeSpan.FromHours(3));
                File.Delete(esd);
                installImages.Add(wim);
            }

            var imagePlan = new List<(string File, ImageInfo Info)>();
            foreach (var file in installImages)
                foreach (var info in await _images.GetImageInfoAsync(file, ct))
                    imagePlan.Add((file, info));

            if (!string.IsNullOrWhiteSpace(options.DonorIso))
            {
                var donor = Path.GetFullPath(options.DonorIso);
                if (donor.Equals(source, StringComparison.OrdinalIgnoreCase) || donor.Equals(output, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Донорский ISO должен отличаться от входного и выходного ISO.");
                Status?.Invoke("Проверяю русский ISO-донор…");
                var donorRoot = await MountIsoAsync(donor, ct);
                donorMounted = true;
                await ImportDonorPackagesAsync(donorRoot, imagePlan, packageRoot, work, ct);
                await DismountIsoAsync(donor);
                donorMounted = false;
            }

            var packages = FindPackages(packageRoot);
            var updatePlan = string.IsNullOrWhiteSpace(options.UpdatesDirectory)
                ? null : await UpdateManifestValidator.LoadAsync(options.UpdatesDirectory, ct: ct);
            var completed = 0;
            foreach (var item in imagePlan)
            {
                ct.ThrowIfCancellationRequested();
                var candidates = await FindVerifiedMatchingPackagesAsync(packages, item.Info, ct);
                if (candidates.Count == 0)
                {
                    Status?.Invoke($"Ищу Client Language Pack ru-RU для {item.Info.BuildFamily}/{item.Info.NormalizedArchitecture}…");
                    var downloaded = await _uup.DownloadRussianLanguagePackAsync(item.Info, packageRoot, _runner,
                        message => Log?.Invoke(message), ct);
                    packages.AddRange(downloaded);
                    candidates = FindMatchingPackages(packages, item.Info);
                }
                if (candidates.Count == 0)
                    throw new FileNotFoundException($"Нет подходящего Client Language Pack для индекса {item.Info.Index}.");

                Status?.Invoke($"Локализую {Path.GetFileName(item.File)}, индекс {item.Info.Index}…");
                await _images.ServiceAsync(item.File, item.Info, candidates, options.FodDirectory,
                    updatePlan, work, message => Log?.Invoke(message), ct,
                    mount => _winre.LocalizeAsync(mount, item.Info.NormalizedArchitecture, options.FodDirectory,
                        updatePlan, ct, message => Log?.Invoke(message)));
                completed++;
                Status?.Invoke($"Обработано индексов: {completed}/{imagePlan.Count}");
            }

            LocalizationStep setupAudit = new("disabled");
            if (options.ProcessSetup)
            {
                Status?.Invoke("Локализую Windows Setup и WinPE…");
                try
                {
                    setupAudit = await _setup.LocalizeAsync(media, imagePlan[0].Info.NormalizedArchitecture,
                        options.FodDirectory, _donorSetupPackages, updatePlan, ct, message => Log?.Invoke(message));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    setupAudit = new LocalizationStep("failed", Error: ex.Message);
                    Log?.Invoke("Setup/WinPE не локализованы; сборка ISO будет продолжена: " + ex.Message);
                }
            }
            var splitReport = await _splitter.SplitOutputAsync(sources, options.SplitWim, ct, message => Log?.Invoke(message));
            ValidateMedia(media);
            var oscdimg = await _adk.EnsureOscdimgAsync(ct, message => Log?.Invoke(message));
            if (File.Exists(output)) throw new IOException("Выходной ISO уже существует; перезапись запрещена.");
            var bios = Path.Combine(media, "boot", "etfsboot.com");
            var uefi = Path.Combine(media, "efi", "microsoft", "boot", "efisys.bin");
            if (!File.Exists(bios) || !File.Exists(uefi))
                throw new InvalidDataException("Для BIOS/UEFI необходимы boot\\etfsboot.com и efi\\microsoft\\boot\\efisys.bin.");

            Status?.Invoke("Создаю загрузочный ISO…");
            var bootData = $"-bootdata:2#p0,e,b{bios}#pEF,e,b{uefi}";
            await _runner.RunAsync(oscdimg, ["-m", "-o", "-u2", "-udfver102", bootData, media, output],
                cancellationToken: ct, timeout: TimeSpan.FromHours(3));
            if (!File.Exists(output) || new FileInfo(output).Length < 1024 * 1024)
                throw new IOException("oscdimg не создал выходной ISO.");

            Status?.Invoke("Повторно подключаю ISO и проверяю WIM…");
            var verification = await VerifyOutputIsoAsync(output, ct);
            HyperVTestResult? hypervAudit = null;
            if (options.RunHyperVTest)
            {
                Status?.Invoke("Проверяю загрузку ISO в Hyper-V…");
                hypervAudit = await _hyperv.RunAsync(output, work,
                    ConfirmHyperVSetup ?? (_ => Task.FromResult(false)), ct, message => Log?.Invoke(message));
            }
            var audit = new
            {
                application = AppInfo.Name,
                version = AppInfo.Version,
                created_at = DateTimeOffset.Now,
                source_iso = source,
                output_iso = output,
                image_language_checks = _images.Audit,
                setup_winpe_localization = setupAudit,
                split_wim = splitReport,
                hyperv_smoke_test = hypervAudit,
                output_static_validation = verification,
                limitations = new[] { "Hyper-V screen verification is operator-confirmed, not automated." }
            };
            var auditPath = Path.ChangeExtension(output, ".localization-audit.json");
            await File.WriteAllTextAsync(auditPath, JsonSerializer.Serialize(audit, new JsonSerializerOptions { WriteIndented = true }), ct);

            if (!options.KeepWorkFiles)
            {
                try { Directory.Delete(work, true); }
                catch (Exception ex) { Log?.Invoke("Не удалось удалить временную папку: " + ex.Message); }
            }
            Status?.Invoke("Готово.");
            Log?.Invoke($"Создан ISO: {output}");
            Log?.Invoke($"JSON-аудит: {auditPath}");
            return output;
        }
        catch
        {
            if (sourceMounted) await SafeDismountAsync(source);
            if (donorMounted && options.DonorIso is not null) await SafeDismountAsync(options.DonorIso);
            throw;
        }
    }

    public async Task<string> PatchExistingWimAsync(string sourceWim, string outputWim, RunOptions options, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Обработка WIM доступна только в Windows.");
        EnsureWindowsAdmin();
        sourceWim = Path.GetFullPath(sourceWim);
        outputWim = Path.GetFullPath(outputWim);
        if (!sourceWim.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) || !File.Exists(sourceWim))
            throw new FileNotFoundException("Для режима patch-existing требуется существующий WIM.", sourceWim);
        if (!outputWim.EndsWith(".wim", StringComparison.OrdinalIgnoreCase) ||
            sourceWim.Equals(outputWim, StringComparison.OrdinalIgnoreCase) || File.Exists(outputWim))
            throw new InvalidOperationException("Выходной WIM должен быть новым файлом, отличным от исходного.");
        var outputFolder = Path.GetDirectoryName(outputWim)!;
        if (!Directory.Exists(outputFolder)) throw new DirectoryNotFoundException("Каталог выходного WIM не существует.");
        if (!Directory.Exists(options.WorkDirectory)) Directory.CreateDirectory(options.WorkDirectory);
        Directory.CreateDirectory(options.PackageDirectory);
        if (options.FodDirectory is not null && !Directory.Exists(options.FodDirectory))
            throw new DirectoryNotFoundException("FOD должен быть существующим каталогом.");
        var work = Path.Combine(options.WorkDirectory, "LocaLizard_PatchWIM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(work);
        var staged = Path.Combine(work, Path.GetFileName(sourceWim));
        File.Copy(sourceWim, staged);
        var sourceInfos = await _images.GetImageInfoAsync(staged, ct);
        var imagePlan = sourceInfos.Select(info => (File: staged, Info: info)).ToList();
        _donorSetupPackages.Clear();
        if (!string.IsNullOrWhiteSpace(options.DonorIso))
        {
            var donor = Path.GetFullPath(options.DonorIso);
            if (!File.Exists(donor) || donor.Equals(sourceWim, StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("Русский ISO-донор не найден или совпадает с исходным WIM.", donor);
            var donorRoot = await MountIsoAsync(donor, ct);
            try { await ImportDonorPackagesAsync(donorRoot, imagePlan, options.PackageDirectory, work, ct); }
            finally { await DismountIsoAsync(donor); }
        }

        var updatePlan = string.IsNullOrWhiteSpace(options.UpdatesDirectory)
            ? null : await UpdateManifestValidator.LoadAsync(options.UpdatesDirectory, ct: ct);
        var packages = FindPackages(options.PackageDirectory);
        for (var i = 0; i < imagePlan.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = imagePlan[i];
            var candidates = FindMatchingPackages(packages, item.Info);
            if (candidates.Count == 0)
            {
                Status?.Invoke($"Ищу Client Language Pack ru-RU для индекса {item.Info.Index}…");
                packages.AddRange(await _uup.DownloadRussianLanguagePackAsync(item.Info, options.PackageDirectory,
                    _runner, message => Log?.Invoke(message), ct));
                candidates = FindMatchingPackages(packages, item.Info);
            }
            if (candidates.Count == 0)
                throw new FileNotFoundException($"Нет проверенного Client Language Pack ru-RU для индекса {item.Info.Index}.");
            Status?.Invoke($"Обрабатываю существующий WIM: индекс {item.Info.Index} ({i + 1}/{imagePlan.Count})…");
            await _images.ServiceAsync(staged, item.Info, candidates, options.FodDirectory, updatePlan, work,
                message => Log?.Invoke(message), ct,
                mount => _winre.LocalizeAsync(mount, item.Info.NormalizedArchitecture, options.FodDirectory,
                    updatePlan, ct, message => Log?.Invoke(message)));
        }

        var stagedInfos = await _images.GetImageInfoAsync(staged, ct);
        if (stagedInfos.Count != sourceInfos.Count)
            throw new InvalidDataException("После обслуживания изменилось число WIM-индексов; публикация результата отменена.");
        var pending = outputWim + ".pending";
        try
        {
            File.Copy(staged, pending, true);
            var pendingInfos = await _images.GetImageInfoAsync(pending, ct);
            if (pendingInfos.Count != sourceInfos.Count)
                throw new InvalidDataException("Повторная проверка выходного WIM не прошла; итоговый файл не опубликован.");
            File.Move(pending, outputWim);
        }
        finally { try { File.Delete(pending); } catch { } }

        var report = new
        {
            application = AppInfo.Name,
            version = AppInfo.Version,
            created_at = DateTimeOffset.Now,
            mode = "patch-existing-wim",
            source_wim = sourceWim,
            output_wim = outputWim,
            indexes_processed = stagedInfos,
            image_language_checks = _images.Audit,
            update_plan = updatePlan?.Packages.Select(x => new { x.File, x.Kind, x.Sha256 }),
            limitations = new[] { "The WIM-only mode does not modify boot.wim or Windows Setup resources." }
        };
        await File.WriteAllTextAsync(Path.ChangeExtension(outputWim, ".localization-audit.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!options.KeepWorkFiles)
        {
            try { Directory.Delete(work, true); }
            catch (Exception ex) { Log?.Invoke("Не удалось удалить рабочую папку: " + ex.Message); }
        }
        Log?.Invoke("Обработанный WIM прошёл повторную проверку: " + outputWim);
        return outputWim;
    }

    private async Task ImportDonorPackagesAsync(
        string donorRoot, IReadOnlyList<(string File, ImageInfo Info)> targets, string packageRoot, string work, CancellationToken ct)
    {
        var donorSources = Path.Combine(donorRoot, "sources");
        var donorImages = Directory.Exists(donorSources)
            ? Directory.EnumerateFiles(donorSources, "install*.wim")
                .Concat(Directory.EnumerateFiles(donorSources, "install*.esd")).ToArray()
            : Array.Empty<string>();
        var donorInfo = new List<ImageInfo>();
        foreach (var file in donorImages)
        {
            try { donorInfo.AddRange(await _images.GetImageInfoAsync(file, ct)); }
            catch (Exception ex) { Log?.Invoke($"Пропускаю метаданные {Path.GetFileName(file)}: {ex.Message}"); }
        }

        var compatible = targets.Any(t => donorInfo.Any(d =>
            d.BuildFamily == t.Info.BuildFamily && d.NormalizedArchitecture == t.Info.NormalizedArchitecture));
        if (!compatible)
        {
            Log?.Invoke("Донорский ISO несовместим по семейству сборки/архитектуре; пакеты не импортированы.");
            return;
        }

        var mediaSources = Path.Combine(work, "media", "sources");
        var donorRu = Path.Combine(donorSources, "ru-ru");
        var targetRu = Path.Combine(mediaSources, "ru-ru");
        if (Directory.Exists(donorRu) && (!Directory.Exists(targetRu) || !Directory.EnumerateFileSystemEntries(targetRu).Any()))
        {
            Directory.CreateDirectory(targetRu);
            foreach (var item in Directory.EnumerateFiles(donorRu, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(donorRu, item);
                var copyTo = Path.Combine(targetRu, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copyTo)!);
                File.Copy(item, copyTo, true);
            }
            Log?.Invoke("Совместимые localized Setup resources скопированы в sources\\ru-ru.");
        }
        var setupDestination = Path.Combine(work, "donor_setup_packages");
        Directory.CreateDirectory(setupDestination);
        foreach (var file in Directory.EnumerateFiles(donorRoot, "*.cab", SearchOption.AllDirectories))
        {
            var lowName = Path.GetFileName(file).ToLowerInvariant();
            var lowPath = file.ToLowerInvariant();
            if ((lowName == "lp.cab" || lowName.Contains("winpe") || lowName.Contains("setup")) &&
                lowPath.Contains("ru-ru", StringComparison.Ordinal))
            {
                var setupCopy = Path.Combine(setupDestination, Path.GetFileName(file));
                if (!File.Exists(setupCopy)) File.Copy(file, setupCopy);
            }
        }
        _donorSetupPackages.AddRange(Directory.EnumerateFiles(setupDestination, "*.cab")
            .Distinct(StringComparer.OrdinalIgnoreCase));

        var destination = Path.Combine(work, "donor_packages");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(donorRoot, "*.cab", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var low = name.ToLowerInvariant();
            if (!low.Contains("ru-ru") || new[] { "server", "winpe", "setup", "recovery" }.Any(low.Contains)) continue;
            var target = Path.Combine(destination, name);
            if (!File.Exists(target)) File.Copy(file, target);
        }
        Directory.CreateDirectory(packageRoot);
        foreach (var file in FindPackages(destination))
        {
            var target = Path.Combine(packageRoot, Path.GetFileName(file));
            if (!File.Exists(target)) File.Copy(file, target);
        }
        Log?.Invoke("Кандидаты из совместимого ISO-донора скопированы в кэш.");
    }

    private async Task<string> MountIsoAsync(string iso, CancellationToken ct)
    {
        var escaped = iso.Replace("'", "''");
        var script = "$d = Mount-DiskImage -StorageType ISO -ImagePath '" + escaped +
                     "' -PassThru -ErrorAction Stop | Get-Volume | Where-Object DriveLetter | Select-Object -First 1; " +
                     "if (-not $d) { throw 'ISO volume has no drive letter' }; Write-Output ($d.DriveLetter + ':')";
        var result = await _runner.RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(3));
        var drive = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.Length >= 2 && char.IsLetter(x[0]) && x[1] == ':');
        return drive is null ? throw new IOException("Не удалось определить букву смонтированного ISO.") : drive + "\\";
    }

    private async Task DismountIsoAsync(string iso)
    {
        var command = "Dismount-DiskImage -ImagePath '" + iso.Replace("'", "''") + "' -ErrorAction SilentlyContinue";
        await _runner.RunAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command],
            cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(2));
    }

    private async Task SafeDismountAsync(string iso)
    {
        try { await DismountIsoAsync(iso); }
        catch (Exception ex) { Log?.Invoke("Ошибка отключения ISO: " + ex.Message); }
    }

    private async Task<Dictionary<string, int>> VerifyOutputIsoAsync(string output, CancellationToken ct)
    {
        var root = await MountIsoAsync(output, ct);
        try
        {
            var sources = Path.Combine(root, "sources");
            if (!File.Exists(Path.Combine(sources, "boot.wim")))
                throw new InvalidDataException("В выходном ISO отсутствует boot.wim.");
            var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(sources, "install*.wim"))
                indexes[Path.GetFileName(file)] = (await _images.GetImageInfoAsync(file, ct)).Count;
            var stems = Directory.EnumerateFiles(sources, "install*.swm")
                .Select(p => Regex.Match(Path.GetFileName(p), "^(install.*?)\\.swm$", RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups[1].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var stem in stems)
            {
                var first = Path.Combine(sources, stem + ".swm");
                var pattern = Path.Combine(sources, stem + "*.swm");
                indexes[stem + ".swm"] = (await _images.GetImageInfoAsync(first, ct, pattern)).Count;
                _ = WimSplitService.Sequence(sources, stem);
            }
            if (indexes.Count == 0) throw new InvalidDataException("В выходном ISO отсутствуют install.wim/install*.swm.");
            _ = await _images.GetImageInfoAsync(Path.Combine(sources, "boot.wim"), ct);
            return indexes;
        }
        finally { await DismountIsoAsync(output); }
    }

    private async Task<List<string>> FindVerifiedMatchingPackagesAsync(IEnumerable<string> all, ImageInfo image, CancellationToken ct)
    {
        var verified = new List<string>();
        foreach (var candidate in FindMatchingPackages(all, image))
        {
            ct.ThrowIfCancellationRequested();
            if (await UupDumpClient.VerifyManagedCacheFileAsync(candidate, ct))
                verified.Add(candidate);
            else
                Log?.Invoke($"Автоматический кэш пакета повреждён или не подтверждён манифестом: {Path.GetFileName(candidate)}.");
        }
        return verified;
    }

    private static List<string> FindPackages(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) ||
                            p.EndsWith(".mum", StringComparison.OrdinalIgnoreCase)).ToList()
            : [];

    private static List<string> FindMatchingPackages(IEnumerable<string> all, ImageInfo image) =>
        all.Where(p => WindowsImageService.IsClientLp(p, image.NormalizedArchitecture))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static void ValidateMedia(string media)
    {
        var sources = Path.Combine(media, "sources");
        var hasWim = Directory.EnumerateFiles(sources, "install*.wim").Any();
        var hasSwm = File.Exists(Path.Combine(sources, "install.swm"));
        if (!File.Exists(Path.Combine(sources, "boot.wim")) || (!hasWim && !hasSwm) ||
            !File.Exists(Path.Combine(media, "setup.exe")))
            throw new InvalidDataException("Структура установочного ISO не прошла проверку.");
    }

    private static string? FindOscdimg()
    {
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        }.Where(Directory.Exists))
        {
            var adk = Path.Combine(root, "Windows Kits", "10", "Assessment and Deployment Kit", "Deployment Tools");
            if (!Directory.Exists(adk)) continue;
            var path = Directory.EnumerateFiles(adk, "oscdimg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (path is not null) return path;
        }
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(p => Path.Combine(p, "oscdimg.exe")).FirstOrDefault(File.Exists);
    }

    private static void EnsureWindowsAdmin()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Обработка ISO доступна только в Windows 10/11.");
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Перезапустите LocaLizard от имени администратора для операций DISM.");
    }

    private static void Validate(RunOptions o)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("LocaLizard предназначен для Windows.");
        if (!File.Exists(o.SourceIso) || !o.SourceIso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("Укажите существующий входной ISO.", o.SourceIso);
        if (!o.OutputIso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Выходной файл должен иметь расширение ISO.");
        var source = Path.GetFullPath(o.SourceIso);
        var output = Path.GetFullPath(o.OutputIso);
        if (source.Equals(output, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Входной и выходной ISO должны отличаться.");
        if (File.Exists(output)) throw new IOException("Выходной ISO уже существует; перезапись запрещена.");
        if (!Directory.Exists(Path.GetDirectoryName(output))) throw new DirectoryNotFoundException("Папка выходного ISO не существует.");
        if (o.DonorIso is not null && !File.Exists(o.DonorIso)) throw new FileNotFoundException("Русский ISO-донор не найден.", o.DonorIso);
        if (o.FodDirectory is not null && !Directory.Exists(o.FodDirectory)) throw new DirectoryNotFoundException("FOD должен быть каталогом Language and Optional Features.");
        if (o.UpdatesDirectory is not null && !Directory.Exists(o.UpdatesDirectory)) throw new DirectoryNotFoundException("Каталог обновлений не найден.");
    }
}
