using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocaLizard.Core;

public enum LanguageSourceKind { Automatic, ManualImport, Reference }

public sealed record LanguageResourceSourceDescriptor(
    string SourceId, string DisplayName, LanguageSourceKind Kind,
    string ResourceTypes, string Description, int Priority,
    bool EnabledByDefault, string? InformationUrl = null);

public static class LanguageSourceCatalog
{
    public static IReadOnlyList<LanguageResourceSourceDescriptor> All { get; } =
    [
        new("w10mui", "W10MUI / BatUtil", LanguageSourceKind.Automatic, "Client/Server LP, FoD",
            "Каталоги сборок и альтернативные списки; бинарные файлы загружаются только с Microsoft CDN.", 10, true,
            "https://github.com/abbodi1406/BatUtil/tree/master/W10MUI"),
        new("osbuilder-public", "OSBuilder.Public", LanguageSourceKind.Automatic, "Исторические LP, LIP, FoD",
            "Исторические CLIXML-каталоги; пакеты проходят проверку Microsoft.", 20, true,
            "https://github.com/OSDeploy/OSBuilder.Public/tree/master/Content/Updates"),
        new("dexit-gist", "Dexit — Windows 10 RS1", LanguageSourceKind.Automatic, "Client LP · build 14393",
            "Закреплённый Gist; только Windows 10 build 14393 x86/x64.", 30, true,
            "https://gist.github.com/dexit/87f30cffa70ea6899177f536a8dc788f"),
        new("pcportal", "PCPortal", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-раздел Windows; скачайте файл вручную и импортируйте с проверкой хеша/подписи.", 100, false,
            "https://pcportal.org/forum/61-10779-1"),
        new("rutracker", "RuTracker", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Торрент-каталог требует ручного выбора и проверки раздачи; автоматическое скачивание не используется.", 101, false,
            "https://rutracker.org/forum/tracker.php?nm=Windows%20language%20pack"),
        new("oldnew", "OldNew", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-архив; импортируйте только самостоятельно проверенные файлы.", 102, false,
            "https://oldnew.ru/"),
        new("techygeekshome", "TechyGeeksHome", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-архив; ссылки и состав пакетов проверяются вручную.", 103, false,
            "https://www.techygeekshome.co.uk/"),
        new("habr-timeweb", "Habr / Timeweb", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Статьи и пользовательские зеркала; импортируйте локальный файл и проверьте контрольную сумму.", 104, false,
            "https://habr.com/ru/search/?q=Windows%20language%20pack"),
        new("mail-cloud", "Mail Cloud", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Пользовательские ссылки Mail Cloud; авторизация и временные ссылки выполняются вне приложения.", 105, false,
            "https://cloud.mail.ru/"),
        new("pixeldrain", "Pixeldrain", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Внешнее файловое хранилище; файл загружается пользователем и проверяется до применения.", 106, false,
            "https://pixeldrain.com/"),
        new("massgrave", "Massgrave", LanguageSourceKind.ManualImport, "Справка / ISO",
            "Справочный community-портал; неподписанные сценарии и исполняемые файлы не импортируются.", 107, false,
            "https://massgrave.dev/"),
        new("comss", "Comss", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-архив; импортируйте локальный файл с проверкой имени, хеша и подписи.", 108, false,
            "https://www.comss.ru/"),
        new("dowsoft", "DowSoft", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-каталог; автоматические загрузки и обход ограничений не выполняются.", 109, false,
            "https://dowsoft.ru/"),
        new("nnm-club", "NNM-Club", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Трекер требует ручного выбора и получения раздачи пользователем.", 110, false,
            "https://nnmclub.to/"),
        new("usbtor", "USBtor", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Community-каталог образов; используйте ручной импорт локального ISO/CAB/ESD.", 111, false,
            "https://usbtor.ru/"),
        new("my-digital-life", "My Digital Life", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Форум сообщества; доступ к вложениям и файлам выполняется вручную.", 112, false,
            "https://forums.mydigitallife.net/"),
        new("elevenforum", "ElevenForum", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Сообщество Windows 11; локальные пакеты проходят общие проверки LocaLizard.", 113, false,
            "https://www.elevenforum.com/"),
        new("fydexx-rs5", "fydexx — Windows 10 RS5 build 17763", LanguageSourceKind.ManualImport, "Client/Server LP · SHA-1",
            "Закреплённый Gist содержит SHA-1 для LP RS5. Импорт проверяет точное имя и SHA-1; ссылки MirrorAce не скачиваются автоматически.", 114, false,
            "https://gist.github.com/fydexx/020e34f98df023d5970f353a443fe396"),
        new("what-the-hack", "Microsoft WhatTheHack", LanguageSourceKind.Reference, "Language ISO / FOD ISO",
            "Читается закреплённый скрипт для исторических Microsoft ISO Windows 10 20H2; скрипт не запускается.", 200, false,
            "https://github.com/microsoft/WhatTheHack/blob/53868d7819be6c83a507ecd6c2971a4b1d4de286/037-AzureVirtualDesktop/Coach/Solutions/04-installLanguagePacks.ps1"),
        new("rg-adguard-files", "RG-Adguard Files", LanguageSourceKind.Reference, "Имя файла · SHA-1/SHA-256",
            "Поиск точного имени CAB возвращает независимые хеши/размер. Файлы не загружаются с RG-Adguard; при импорте хеш сверяется.", 201, false,
            "https://files.rg-adguard.net/search"),
        new("m417z-language-files", "m417z — Windows language files", LanguageSourceKind.Reference, "Извлечённые MUI-ресурсы",
            "Исходники MUI/RC для разработки и диагностики; это не установочные CAB/ESD и не источник пакетов для DISM.", 202, false,
            "https://github.com/m417z/windows-language-files")
    ];

    public static IReadOnlyList<LanguageResourceSourceDescriptor> AutomaticSources =>
        All.Where(x => x.Kind == LanguageSourceKind.Automatic).OrderBy(x => x.Priority).ToArray();
    public static IReadOnlyList<LanguageResourceSourceDescriptor> ManualSources =>
        All.Where(x => x.Kind == LanguageSourceKind.ManualImport).OrderBy(x => x.Priority).ToArray();
    public static IReadOnlyList<LanguageResourceSourceDescriptor> ReferenceSources =>
        All.Where(x => x.Kind == LanguageSourceKind.Reference).OrderBy(x => x.Priority).ToArray();
    public static LanguageResourceSourceDescriptor? Find(string sourceId) =>
        All.FirstOrDefault(x => x.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
}

public sealed class LanguageSourceConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    public LanguageSourceConfiguration()
    {
        foreach (var source in LanguageSourceCatalog.AutomaticSources)
            _enabled[source.SourceId] = source.EnabledByDefault;
    }

    public int EnabledAutomaticSourceCount => _enabled.Count(x => x.Value);
    public bool IsEnabled(string sourceId) =>
        _enabled.TryGetValue(sourceId, out var enabled) ? enabled :
        LanguageSourceCatalog.Find(sourceId)?.EnabledByDefault == true;

    public void SetEnabled(string sourceId, bool enabled)
    {
        if (LanguageSourceCatalog.Find(sourceId) is not { Kind: LanguageSourceKind.Automatic })
            throw new ArgumentException("Настраивать можно только автоматические источники.", nameof(sourceId));
        _enabled[sourceId] = enabled;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocaLizard", "language-sources.json");

    public static LanguageSourceConfiguration Load(string? path = null)
    {
        var configuration = new LanguageSourceConfiguration();
        var fullPath = Path.GetFullPath(path ?? DefaultPath);
        if (!File.Exists(fullPath)) return configuration;
        try
        {
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(File.ReadAllText(fullPath), JsonOptions);
            if (document is not { SchemaVersion: 1, EnabledSources: not null }) return configuration;
            foreach (var item in document.EnabledSources)
                if (LanguageSourceCatalog.Find(item.Key) is { Kind: LanguageSourceKind.Automatic })
                    configuration._enabled[item.Key] = item.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Corrupt optional preferences fall back to defaults.
        }
        return configuration;
    }

    public void Save(string? path = null)
    {
        var fullPath = Path.GetFullPath(path ?? DefaultPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Не удалось определить каталог настроек источников.");
        Directory.CreateDirectory(directory);
        var temporary = fullPath + ".tmp";
        var document = new ConfigurationDocument
        {
            SchemaVersion = 1,
            EnabledSources = _enabled.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
        };
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
            File.Move(temporary, fullPath, true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private sealed class ConfigurationDocument
    {
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
        [JsonPropertyName("enabled_sources")] public Dictionary<string, bool>? EnabledSources { get; init; }
    }
}

public static class LanguageSourceFactory
{
    public static ResourceSourceRegistry CreateDefault(LanguageSourceConfiguration? configuration = null)
    {
        configuration ??= LanguageSourceConfiguration.Load();
        var registry = new ResourceSourceRegistry();
        try
        {
            foreach (var descriptor in LanguageSourceCatalog.AutomaticSources
                         .Where(source => configuration.IsEnabled(source.SourceId)).OrderBy(x => x.Priority))
            {
                ILanguageResourceProvider provider = descriptor.SourceId switch
                {
                    "w10mui" => new W10MuiCatalogProvider(),
                    "osbuilder-public" => new OsBuilderCatalogProvider(),
                    "dexit-gist" => new DexitGistProvider(),
                    _ => throw new InvalidOperationException($"Нет провайдера для {descriptor.SourceId}.")
                };
                registry.Register(provider);
            }
            return registry;
        }
        catch { registry.Dispose(); throw; }
    }
}
