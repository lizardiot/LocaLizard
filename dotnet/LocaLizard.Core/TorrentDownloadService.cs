using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LocaLizard.Core;

public sealed record TorrentDownloadResult(string DownloadDirectory, IReadOnlyList<string> PackageFiles);

/// <summary>
/// Optional BitTorrent hand-off to a locally installed aria2c. LocaLizard never
/// executes content from a torrent; only supported package/media files are offered
/// to the existing hash/signature/CBS import pipeline.
/// </summary>
public sealed class TorrentDownloadService
{
    private const long MaximumTorrentMetadataBytes = 20L * 1024 * 1024;
    private const long MaximumTorrentPayloadBytes = 48L * 1024 * 1024 * 1024;

    public static bool IsValidMagnetUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 ||
            value.Any(char.IsControl) || !value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return false;
        return Regex.IsMatch(value,
            @"(?i)(?:\?|&)xt=urn:btih:(?:[a-f0-9]{40}|[a-z2-7]{32})(?:&|$)");
    }

    public static bool IsValidTorrentInput(string? value)
    {
        if (IsValidMagnetUri(value)) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var path = Path.GetFullPath(value.Trim().Trim('"'));
            if (!Path.GetExtension(path).Equals(".torrent", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path)) return false;
            var info = new FileInfo(path);
            return info.Length is > 0 and <= MaximumTorrentMetadataBytes &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static string? FindAria2Executable()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "aria2c.exe");
        if (File.Exists(local)) return local;
        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "aria2c.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Skip malformed PATH entries.
            }
        }
        return null;
    }

    public async Task<TorrentDownloadResult> DownloadAsync(
        string torrentInput,
        string packageRoot,
        CancellationToken ct,
        Action<string>? log = null)
    {
        if (!IsValidTorrentInput(torrentInput))
            throw new InvalidDataException("Укажите корректную magnet-ссылку BitTorrent или локальный файл .torrent.");
        var executable = FindAria2Executable()
            ?? throw new FileNotFoundException("aria2c.exe не найден. Установите aria2 и добавьте каталог с aria2c.exe в PATH либо рядом с LocaLizard.exe.");

        var directory = Path.Combine(Path.GetFullPath(packageRoot), "ManualImport", "TorrentDownloads",
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "--dir", directory,
            "--seed-time=0",
            "--max-tries=5",
            "--retry-wait=5",
            "--check-integrity=true",
            "--allow-overwrite=false",
            "--auto-file-renaming=true",
            "--file-allocation=none",
            "--console-log-level=notice",
            "--summary-interval=5"
        })
            startInfo.ArgumentList.Add(argument);

        var input = torrentInput.Trim().Trim('"');
        startInfo.ArgumentList.Add(IsValidMagnetUri(input) ? input : Path.GetFullPath(input));

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
            throw new InvalidOperationException("Не удалось запустить aria2c.");

        var stdoutTask = PumpOutputAsync(process.StandardOutput, log, ct);
        var stderrTask = PumpOutputAsync(process.StandardError, log, ct);
        try
        {
            var exitTask = process.WaitForExitAsync(ct);
            while (!exitTask.IsCompleted)
            {
                await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(2), ct));
                if (exitTask.IsCompleted) break;
                if (GetSafeTreeSize(directory, MaximumTorrentPayloadBytes) > MaximumTorrentPayloadBytes)
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                    await process.WaitForExitAsync(CancellationToken.None);
                    throw new InvalidDataException("BitTorrent payload exceeded the 48 GiB safety limit.");
                }
            }
            await exitTask;
            await Task.WhenAll(stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"aria2c завершился с кодом {process.ExitCode}. Проверьте раздачу и доступность трекеров.");

        var packages = new List<string>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(directory);
        while (pendingDirectories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(currentDirectory).ToArray(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirectories.Push(entry);
                        continue;
                    }
                    if (ManualPackageImportService.IsSupportedImportExtension(Path.GetFileName(entry)))
                        packages.Add(entry);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        if (packages.Count == 0)
            throw new InvalidDataException("Раздача завершилась, но не содержит поддерживаемых CAB/ESD/ISO/APPX/MSIX/License.xml.");
        log?.Invoke($"BitTorrent: найдено поддерживаемых файлов: {packages.Count}. Они ещё не импортированы и должны пройти общую проверку LocaLizard.");
        return new TorrentDownloadResult(directory, packages);
    }

    private static long GetSafeTreeSize(string root, long stopAfterBytes)
    {
        var total = 0L;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string[] entries;
            try { entries = Directory.GetFileSystemEntries(pending.Pop()); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var entry in entries)
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    total = checked(total + new FileInfo(entry).Length);
                    if (total > stopAfterBytes) return total;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return total;
    }

    private static async Task PumpOutputAsync(
        StreamReader reader, Action<string>? log, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
                log?.Invoke(line.Length > 500 ? line[..500] : line);
        }
    }
}
