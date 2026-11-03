using System.Text.Json;
using LocaLizard.Core;

namespace LocaLizard.App;

public sealed record ElevatedLaunchRequest(
    string Mode,
    RunOptions Options,
    string? SourceWim = null,
    string? OutputWim = null,
    string? KnowledgeBase = null);

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        ElevatedLaunchRequest? request = null;
        try
        {
            if (args.Length > 0)
            {
                if (args.Length != 2 || !args[0].Equals("--elevated", StringComparison.Ordinal))
                    throw new ArgumentException("Неверные параметры запуска LocaLizard.");
                var payload = Convert.FromBase64String(args[1]);
                request = JsonSerializer.Deserialize<ElevatedLaunchRequest>(payload)
                    ?? throw new InvalidDataException("Не удалось восстановить параметры повышенного запуска.");
                if (request.Mode is not ("build" or "patch" or "catalog" or "preflight"))
                    throw new InvalidDataException("Неизвестная операция повышенного запуска.");
            }
            Application.Run(new MainForm(request));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
