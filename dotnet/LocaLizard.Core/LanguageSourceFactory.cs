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
        new("community-import", "Community Source Import", LanguageSourceKind.ManualImport, "CAB, ESD, ISO",
            "Только ручной импорт: PCPortal, RuTracker, OldNew, TechyGeeksHome, Habr/Timeweb, Mail Cloud, Pixeldrain, Massgrave, Comss, DowSoft, NNM-Club, USBtor, My Digital Life и ElevenForum.",
            100, false),
        new("fydexx-rs5", "fydexx — Windows 10 RS5", LanguageSourceKind.ManualImport, "Исторические LP / SHA-1",
            "Отдельный актуальный машиночитаемый каталог не подтверждён; использовать только ручной импорт.", 110, false),
        new("what-the-hack", "Microsoft WhatTheHack", LanguageSourceKind.Reference, "Скрипт и инструкции",
            "Справочный пример; скрипты не выполняются и не скачиваются автоматически.", 200, false,
            "https://github.com/microsoft/WhatTheHack/blob/master/037-AzureVirtualDesktop/Coach/Solutions/04-installLanguagePacks.ps1"),
        new("rg-adguard-files", "RG-Adguard Files", LanguageSourceKind.Reference, "Метаданные файлов",
            "Справочная индексация выпусков, размеров и хешей; не является доверенным бинарным хранилищем.", 210, false,
            "https://files.rg-adguard.net/"),
        new("m417z-language-files", "m417z — Windows language files", LanguageSourceKind.Reference, "Извлечённые MUI-ресурсы",
            "Справочник для диагностики; извлечённые .mui.rc не являются установочными CAB/ESD.", 220, false,
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
                         .Where(configuration.IsEnabled).OrderBy(x => x.Priority))
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
