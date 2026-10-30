using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public static class UpdateManifestValidator
{
    private static readonly HashSet<string> AllowedKinds = new(StringComparer.Ordinal)
    {
        "checkpoint", "target_lcu", "winre_ssu", "safeos_dynamic_update"
    };

    public static async Task<UpdatePlan> LoadAsync(string folder, ImageInfo? image = null, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(folder);
        var manifestPath = Path.Combine(root, "updates.json");
        if (!Directory.Exists(root) || !File.Exists(manifestPath))
            throw new InvalidDataException("Требуется каталог обновлений с updates.json.");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, ct));
        var json = doc.RootElement;
        if (json.GetProperty("schema_version").GetInt32() != 1)
            throw new InvalidDataException("В updates.json требуется schema_version: 1.");
        var build = json.GetProperty("target_build_family").GetInt32();
        var arch = BuildNumbers.Architecture(json.GetProperty("architecture").GetString());
        if (build < 7600 || !BuildNumbers.IsSupportedArchitecture(arch))
            throw new InvalidDataException("Некорректные target_build_family или architecture в updates.json.");
        if (image is not null && (image.BuildFamily != build || image.NormalizedArchitecture != arch))
            throw new InvalidDataException($"Обновления {build}/{arch} несовместимы с индексом {image.Index} ({image.BuildFamily}/{image.NormalizedArchitecture}).");

        var rows = json.GetProperty("ordered_packages");
        if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0)
            throw new InvalidDataException("ordered_packages должен быть непустым массивом.");

        var result = new List<UpdatePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.EnumerateArray())
        {
            var file = row.GetProperty("file").GetString() ?? "";
            var kind = row.GetProperty("kind").GetString() ?? "";
            var sha = (row.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
            SecurityPolicy.EnsureSafeFileName(root, file);
            if (!AllowedKinds.Contains(kind)) throw new InvalidDataException($"Неизвестный тип обновления: {kind}.");
            if (!(file.EndsWith(".cab", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".msu", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Поддерживаются только CAB/MSU: {file}");
            if (!seen.Add(file)) throw new InvalidDataException($"Повтор пакета: {file}");
            if (!Regex.IsMatch(sha, "^[0-9a-f]{64}$")) throw new InvalidDataException($"Для {file} требуется SHA-256.");
            var path = Path.GetFullPath(Path.Combine(root, file));
            if (!File.Exists(path)) throw new FileNotFoundException($"Файл не найден: {file}", path);
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(sha)))
                throw new InvalidDataException($"SHA-256 не совпадает для {file}.");
            result.Add(new UpdatePackage(file, kind, sha, path));
        }

        var os = result.Where(p => p.Kind is "checkpoint" or "target_lcu").ToArray();
        var target = os.Select((p, i) => (p, i)).Where(x => x.p.Kind == "target_lcu").ToArray();
        if (target.Length != 1 || target[0].i != os.Length - 1)
            throw new InvalidDataException("Нужен ровно один target_lcu, последний после checkpoint-пакетов.");
        return new UpdatePlan(build, arch, result);
    }
}
