using System.Xml;
using System.Xml.Linq;

namespace LocaLizard.Core;

public sealed record PreflightItem(string Level, string Name, string Detail);
public sealed record BuildPreflightReport(
    DateTimeOffset CheckedAt,
    IReadOnlyList<ImageInfo> Images,
    IReadOnlyList<PreflightItem> Items)
{
    public bool CanProceed => Items.All(x => x.Level != "error");
    public bool HasWarnings => Items.Any(x => x.Level == "warning");

    public string ToDisplayString()
    {
        var lines = new List<string>
        {
            $"LocaLizard {AppInfo.Version} — предварительная проверка",
            $"Время: {CheckedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}",
            $"Обнаружено индексов Windows: {Images.Count}"
        };
        lines.AddRange(Images.Select(x =>
        {
            var lifecycle = WindowsLifecycle.GetInfo(x.BuildFamily);
            var suffix = lifecycle is null ? "" : $" | поддержка до {lifecycle.EndOfSupport:dd.MM.yyyy}";
            return $"  Индекс {x.Index}: {x.Name}; build {x.Version}; {x.NormalizedArchitecture}{suffix}";
        }));
        lines.Add("");
        foreach (var item in Items)
            lines.Add($"[{item.Level.ToUpperInvariant()}] {item.Name}: {item.Detail}");
        lines.Add("");
        lines.Add(CanProceed ? "Критических ошибок не найдено." : "Обнаружены ошибки: сборку запускать нельзя.");
        return string.Join(Environment.NewLine, lines);
    }
}

public static class BuildPreflightService
{
    private const long GiB = 1024L * 1024 * 1024;

    public static async Task<BuildPreflightReport> AnalyzeAsync(RunOptions options, CancellationToken ct = default)
    {
        var items = new List<PreflightItem>();
        var images = new List<ImageInfo>();
        void Add(string level, string name, string detail) => items.Add(new PreflightItem(level, name, detail));

        Add("info", "Hyper-V",
            options.RunHyperVTest
                ? "Необязательная проверка загрузки включена; она выполняется после создания и статической проверки ISO."
                : "Необязательная проверка загрузки отключена; она не требуется для создания ISO.");

        if (!string.IsNullOrWhiteSpace(options.LxpDirectory))
        {
            if (!Directory.Exists(options.LxpDirectory))
                Add("error", "Russian LXP", "Каталог APPX/MSIX не найден.");
            else
            {
                var lxpCount = AdkBootstrapper.EnumerateFilesSafely(options.LxpDirectory, "*")
                    .Count(path => new[] { ".appx", ".msix", ".appxbundle", ".msixbundle" }
                        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
                Add(lxpCount > 0 ? "info" : "warning", "Russian LXP",
                    lxpCount > 0 ? $"Найдено пакетов APPX/MSIX: {lxpCount}; подписи, Publisher, ru-RU и зависимости будут проверены перед provision."
                        : "Пакеты APPX/MSIX не найдены; необязательная LXP-интеграция будет пропущена.");
            }
        }
        if (!string.IsNullOrWhiteSpace(options.StoreProductId))
        {
            Add(System.Text.RegularExpressions.Regex.IsMatch(options.StoreProductId.Trim(), @"^[A-Za-z0-9][A-Za-z0-9._-]{7,63}$")
                    ? "info" : "error",
                "Microsoft Store Product ID",
                "Резолвер использует сторонний каталог только для метаданных; бинарный пакет принимается только с разрешённого HTTPS Microsoft CDN и после проверки подписи.");
        }

        if (!OperatingSystem.IsWindows())
        {
            Add("error", "Операционная система", "Для DISM и монтирования ISO требуется Windows.");
            return new BuildPreflightReport(DateTimeOffset.Now, images, items);
        }
        if (string.IsNullOrWhiteSpace(options.SourceIso) || !File.Exists(options.SourceIso))
        {
            Add("error", "Исходный ISO", "Файл не найден.");
            return new BuildPreflightReport(DateTimeOffset.Now, images, items);
        }
        if (!options.SourceIso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            Add("error", "Исходный ISO", "Ожидается файл с расширением .iso.");
        if (string.IsNullOrWhiteSpace(options.OutputIso) || !options.OutputIso.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            Add("error", "Выходной ISO", "Укажите путь с расширением .iso.");
        if (!string.IsNullOrWhiteSpace(options.OutputIso))
        {
            var fullOutput = Path.GetFullPath(options.OutputIso);
            if (fullOutput.Equals(Path.GetFullPath(options.SourceIso), StringComparison.OrdinalIgnoreCase))
                Add("error", "Выходной ISO", "Нельзя перезаписывать исходный ISO.");
            if (File.Exists(fullOutput))
                Add("error", "Выходной ISO", "Файл уже существует; перезапись запрещена.");
            if (!Directory.Exists(Path.GetDirectoryName(fullOutput)))
                Add("error", "Каталог результата", "Каталог выходного ISO не существует.");
        }
        if (string.IsNullOrWhiteSpace(options.WorkDirectory))
            Add("error", "Рабочий каталог", "Не задан.");
        if (string.IsNullOrWhiteSpace(options.PackageDirectory))
            Add("error", "Кэш пакетов", "Не задан.");
        else if (File.Exists(options.PackageDirectory))
            Add("error", "Кэш пакетов", "Вместо каталога указан существующий файл.");
        if (!string.IsNullOrWhiteSpace(options.FodDirectory) && !Directory.Exists(options.FodDirectory))
            Add("error", "FOD", "Выбранный каталог не существует.");
        else if (string.IsNullOrWhiteSpace(options.FodDirectory))
            Add("warning", "FOD", "FOD/L&O не задан. OCR, речь и рукописный ввод не будут добавлены автоматически.");
        if (!string.IsNullOrWhiteSpace(options.FodDirectory) && Directory.Exists(options.FodDirectory))
        {
            var ruComponents = FodInventoryService.FindRussianCapabilityPackages(options.FodDirectory,
                images.FirstOrDefault()?.NormalizedArchitecture ?? "x64").Count;
            var hasRepositoryMetadata = Directory.Exists(Path.Combine(options.FodDirectory, "Metadata")) ||
                                        Directory.Exists(Path.Combine(options.FodDirectory, "metadata")) ||
                                        Path.GetFileName(options.FodDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                            .Equals("LanguagesAndOptionalFeatures", StringComparison.OrdinalIgnoreCase) ||
                                        Path.GetFileName(options.FodDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                                            .Equals("FeaturesOnDemand", StringComparison.OrdinalIgnoreCase);
            Add(ruComponents > 0 ? (hasRepositoryMetadata ? "info" : "warning") : "warning", "RU FOD",
                ruComponents > 0
                    ? hasRepositoryMetadata
                        ? $"Обнаружены русские FOD CAB: {ruComponents}; каталог содержит признаки официального репозитория."
                        : $"Обнаружены русские FOD CAB: {ruComponents}, но не обнаружены Metadata. Плоская папка CAB не считается готовым FOD-репозиторием; DISM /Export-Source должен успешно подготовить источник."
                    : "В каталоге не обнаружены русские FOD CAB для целевой архитектуры.");
        }
        if (!string.IsNullOrWhiteSpace(options.DriverDirectory))
        {
            if (!Directory.Exists(options.DriverDirectory))
                Add("error", "Драйверы", "Каталог INF-драйверов не существует.");
            else if (!Directory.EnumerateFiles(options.DriverDirectory, "*.inf", SearchOption.AllDirectories).Any())
                Add("error", "Драйверы", "В каталоге не найдено ни одного INF-файла.");
            else Add("info", "Драйверы", "INF-файлы будут интегрированы в каждый индекс install.wim.");
        }
        if (!string.IsNullOrWhiteSpace(options.UnattendFile))
        {
            try
            {
                if (!File.Exists(options.UnattendFile)) throw new FileNotFoundException("Файл не найден.");
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(options.UnattendFile, settings);
                var document = XDocument.Load(reader);
                if (!string.Equals(document.Root?.Name.LocalName, "unattend", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Корневой элемент XML должен быть unattend.");
                Add("info", "unattend.xml", "XML корректен. DISM применит выбранный файл к каждому индексу install.wim.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
            {
                Add("error", "unattend.xml", ex.Message);
            }
        }

        var sourceRoot = await MountIsoAsync(options.SourceIso, ct);
        try
        {
            var sources = Path.Combine(sourceRoot, "sources");
            if (!File.Exists(Path.Combine(sources, "boot.wim")))
                Add("error", "boot.wim", "В исходном ISO отсутствует sources\\boot.wim.");
            var installFiles = Directory.Exists(sources)
                ? Directory.EnumerateFiles(sources, "install*.wim")
                    .Concat(Directory.EnumerateFiles(sources, "install*.esd")).ToArray()
                : Array.Empty<string>();
            if (installFiles.Length == 0)
                Add("error", "Install image", "Не найден install.wim или install.esd.");
            var imageService = new WindowsImageService(new ProcessRunner());
            foreach (var file in installFiles)
            {
                try { images.AddRange(await imageService.GetImageInfoAsync(file, ct)); }
                catch (Exception ex) { Add("error", Path.GetFileName(file), "DISM не смог прочитать образ: " + ex.Message); }
            }
        }
        finally { await DismountIsoAsync(options.SourceIso); }

        if (images.Count > 0)
        {
            foreach (var image in images)
            {
                if (image.BuildFamily == 0)
                    Add("error", $"Индекс {image.Index}", $"Не удалось распознать семейство сборки: {image.Version}.");
                if (!BuildNumbers.IsSupportedArchitecture(image.NormalizedArchitecture))
                    Add("error", $"Индекс {image.Index}", $"Архитектура не поддерживается: {image.Architecture}.");
                var lifecycle = WindowsLifecycle.GetInfo(image.BuildFamily);
                if (lifecycle is not null)
                    Add(lifecycle.IsSupported ? "info" : "warning", lifecycle.Product, lifecycle.Message);
                else
                    Add("warning", $"Индекс {image.Index}", "Для этой семьи сборки нет встроенной записи жизненного цикла; проверьте редакцию Windows вручную.");
            }

            if (!string.IsNullOrWhiteSpace(options.UpdatesDirectory))
            {
                try
                {
                    var plan = await UpdateManifestValidator.LoadAsync(options.UpdatesDirectory, ct: ct);
                    var unmatched = images.Where(x => x.BuildFamily != plan.TargetBuildFamily ||
                        x.NormalizedArchitecture != plan.Architecture).ToArray();
                    if (unmatched.Length > 0)
                        Add("error", "updates.json", $"План {plan.TargetBuildFamily}/{plan.Architecture} не подходит для {string.Join(", ", unmatched.Select(x => $"индекса {x.Index} ({x.BuildFamily}/{x.NormalizedArchitecture})"))}.");
                    else
                        Add("info", "updates.json", $"SHA-256 всех {plan.Packages.Count} пакетов совпадает; целевая сборка и архитектура проверены.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or FormatException or KeyNotFoundException)
                {
                    Add("error", "updates.json", ex.Message);
                }
            }
            else Add("warning", "Обновления", "Набор updates.json не выбран. LCU не будет интегрирован из локального набора.");
        }

        if (!string.IsNullOrWhiteSpace(options.DonorIso))
        {
            if (!File.Exists(options.DonorIso))
                Add("error", "ISO-донор", "Файл не найден.");
            else if (Path.GetFullPath(options.DonorIso).Equals(Path.GetFullPath(options.SourceIso), StringComparison.OrdinalIgnoreCase) ||
                     (!string.IsNullOrWhiteSpace(options.OutputIso) && Path.GetFullPath(options.DonorIso).Equals(Path.GetFullPath(options.OutputIso), StringComparison.OrdinalIgnoreCase)))
                Add("error", "ISO-донор", "Донор должен отличаться от исходного и выходного ISO.");
            else
            {
                string? donorRoot = null;
                try
                {
                    donorRoot = await MountIsoAsync(options.DonorIso, ct);
                    var donorSources = Path.Combine(donorRoot, "sources");
                    var donorFiles = Directory.Exists(donorSources)
                        ? Directory.EnumerateFiles(donorSources, "install*.wim")
                            .Concat(Directory.EnumerateFiles(donorSources, "install*.esd")).ToArray()
                        : Array.Empty<string>();
                    var donorImages = new List<ImageInfo>();
                    var imageService = new WindowsImageService(new ProcessRunner());
                    foreach (var file in donorFiles)
                        donorImages.AddRange(await imageService.GetImageInfoAsync(file, ct));
                    var compatible = images.Any(t => donorImages.Any(d =>
                        t.BuildFamily == d.BuildFamily && t.NormalizedArchitecture == d.NormalizedArchitecture));
                    Add(compatible ? "info" : "warning", "Совместимость ISO-донора",
                        compatible ? "Найден совпадающий build family и архитектура." :
                        "Не найдено совпадений по build family и архитектуре. Пакеты донора не будут импортированы.");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
                {
                    Add("error", "ISO-донор", ex.Message);
                }
                finally { if (donorRoot is not null) await DismountIsoAsync(options.DonorIso); }
            }
        }

        if (File.Exists(options.SourceIso) && !string.IsNullOrWhiteSpace(options.WorkDirectory) &&
            !string.IsNullOrWhiteSpace(options.OutputIso) && Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(options.OutputIso))))
        {
            var sourceSize = new FileInfo(options.SourceIso).Length;
            var workRequired = (long)(sourceSize * 3.5) + 8 * GiB;
            var outputRequired = (long)(sourceSize * 1.2) + 512L * 1024 * 1024;
            try
            {
                var workRoot = Path.GetFullPath(options.WorkDirectory);
                var outputRoot = Path.GetFullPath(options.OutputIso);
                var workDrive = new DriveInfo(Path.GetPathRoot(workRoot)!);
                var outputDrive = new DriveInfo(Path.GetPathRoot(outputRoot)!);
                var sameDrive = string.Equals(workDrive.Name, outputDrive.Name, StringComparison.OrdinalIgnoreCase);
                if (sameDrive)
                {
                    var need = workRequired + outputRequired;
                    Add(workDrive.AvailableFreeSpace >= need ? "info" : "error", "Свободное место",
                        $"Диск {workDrive.Name}: доступно {workDrive.AvailableFreeSpace / GiB:N1} GiB, оценка потребности {need / GiB:N1} GiB.");
                }
                else
                {
                    Add(workDrive.AvailableFreeSpace >= workRequired ? "info" : "error", "Рабочий диск",
                        $"Диск {workDrive.Name}: доступно {workDrive.AvailableFreeSpace / GiB:N1} GiB, требуется ориентировочно {workRequired / GiB:N1} GiB.");
                    Add(outputDrive.AvailableFreeSpace >= outputRequired ? "info" : "error", "Диск результата",
                        $"Диск {outputDrive.Name}: доступно {outputDrive.AvailableFreeSpace / GiB:N1} GiB, требуется ориентировочно {outputRequired / GiB:N1} GiB.");
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                Add("error", "Свободное место", ex.Message);
            }
        }

        var oscdimg = AdkBootstrapper.FindOscdimg();
        Add(oscdimg is null ? "warning" : "info", "Windows ADK",
            oscdimg is null
                ? "oscdimg.exe не найден. LocaLizard попробует загрузить и установить официальный ADK с проверкой подписи Microsoft."
                : $"Найден oscdimg.exe {AdkBootstrapper.ReadFileVersion(oscdimg)}: {oscdimg}");
        Add("info", "Публикация", "Готовый ISO будет собран во временный файл, повторно проверен и только затем перемещён к конечному имени.");
        return new BuildPreflightReport(DateTimeOffset.Now, images, items);
    }

    private static async Task<string> MountIsoAsync(string iso, CancellationToken ct)
    {
        var escaped = Path.GetFullPath(iso).Replace("'", "''");
        var script = "$d=Mount-DiskImage -StorageType ISO -ImagePath '" + escaped +
            "' -PassThru -ErrorAction Stop | Get-Volume | Where-Object DriveLetter | Select-Object -First 1; " +
            "if(-not $d){throw 'ISO has no drive letter'}; Write-Output ($d.DriveLetter+':')";
        var result = await new ProcessRunner().RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
            cancellationToken: ct, timeout: TimeSpan.FromMinutes(3));
        var drive = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).LastOrDefault(x => x.Length >= 2 && char.IsLetter(x[0]) && x[1] == ':')
            ?? throw new IOException("Не удалось определить букву смонтированного ISO.");
        return drive + "\\";
    }

    private static async Task DismountIsoAsync(string iso)
    {
        var escaped = Path.GetFullPath(iso).Replace("'", "''");
        var script = "Dismount-DiskImage -ImagePath '" + escaped + "' -ErrorAction SilentlyContinue";
        await new ProcessRunner().RunAsync("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", script],
            cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(2));
    }
}