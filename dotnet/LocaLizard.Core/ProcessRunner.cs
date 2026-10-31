using System.Diagnostics;
using System.Text;

namespace LocaLizard.Core;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ProcessRunner
{
    public event Action<string>? OutputLine;

    public async Task<ProcessResult> RunAsync(
        string executable, IEnumerable<string> arguments, string? workingDirectory = null,
        CancellationToken cancellationToken = default, bool throwOnError = true, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        OutputLine?.Invoke("$ " + executable + " " + string.Join(" ", start.ArgumentList));

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Не удалось запустить {executable}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Не удалось запустить {executable}: {ex.Message}", ex);
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var outTask = PumpAsync(process.StandardOutput, stdout);
        var errTask = PumpAsync(process.StandardError, stderr);
        using var timeoutCts = timeout is null ? null : new CancellationTokenSource(timeout.Value);
        using var linked = timeoutCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(outTask, errTask);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
            try { await Task.WhenAll(outTask, errTask); } catch { }
            if (timeoutCts?.IsCancellationRequested == true && !cancellationToken.IsCancellationRequested)
                throw new TimeoutException($"Истекло время ожидания команды {executable}.");
            throw;
        }

        var result = new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        if (throwOnError && result.ExitCode != 0)
            throw new InvalidOperationException($"Код {result.ExitCode}: {executable}{Environment.NewLine}{result.StandardOutput}{result.StandardError}");
        return result;
    }

    private async Task PumpAsync(StreamReader reader, StringBuilder destination)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            destination.AppendLine(line);
            OutputLine?.Invoke(line);
        }
    }
}
