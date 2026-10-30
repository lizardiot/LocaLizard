using System.Security.Principal;
using LocaLizard.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

if (!OperatingSystem.IsWindows())
    throw new PlatformNotSupportedException("DISM integration tests run only on Windows.");

using (var identity = WindowsIdentity.GetCurrent())
{
    if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        throw new UnauthorizedAccessException("DISM integration tests require an elevated Windows runner.");
}

var root = Path.Combine(Path.GetTempPath(), "LocaLizard_DISM_" + Guid.NewGuid().ToString("N"));
var firstSource = Path.Combine(root, "source-one");
var secondSource = Path.Combine(root, "source-two");
var firstApply = Path.Combine(root, "applied-one");
var secondApply = Path.Combine(root, "applied-two");
var mount = Path.Combine(root, "mount");
var wim = Path.Combine(root, "synthetic-install.wim");
var runner = new ProcessRunner();
var images = new WindowsImageService(runner);
var mounted = false;

try
{
    Directory.CreateDirectory(firstSource);
    Directory.CreateDirectory(secondSource);
    Directory.CreateDirectory(mount);
    await File.WriteAllTextAsync(Path.Combine(firstSource, "payload-one.txt"), "LocaLizard DISM WIM round-trip #1");
    await File.WriteAllTextAsync(Path.Combine(secondSource, "payload-two.txt"), "LocaLizard DISM WIM round-trip #2");

    await runner.RunAsync("dism.exe",
        ["/Capture-Image", $"/ImageFile:{wim}", $"/CaptureDir:{firstSource}",
         "/Name:LocaLizard Integration Image One", "/Compress:fast", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));
    await runner.RunAsync("dism.exe",
        ["/Append-Image", $"/ImageFile:{wim}", $"/CaptureDir:{secondSource}",
         "/Name:LocaLizard Integration Image Two", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));

    var inventory = await images.GetImageInfoAsync(wim, CancellationToken.None);
    Check(inventory.Count == 2, "WindowsImageService enumerates both DISM WIM indexes");
    Check(inventory[0].Name.Contains("Image One", StringComparison.Ordinal), "first WIM index metadata");
    Check(inventory[1].Name.Contains("Image Two", StringComparison.Ordinal), "second WIM index metadata");

    await runner.RunAsync("dism.exe",
        ["/Mount-Image", $"/ImageFile:{wim}", "/Index:1", $"/MountDir:{mount}", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));
    mounted = true;
    var mountedPayload = Path.Combine(mount, "payload-one.txt");
    Check(File.Exists(mountedPayload), "mount exposes captured WIM content");
    await File.WriteAllTextAsync(Path.Combine(mount, "committed-by-localizard.txt"), "committed");
    await runner.RunAsync("dism.exe",
        ["/Unmount-Image", $"/MountDir:{mount}", "/Commit", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));
    mounted = false;

    var afterCommit = await images.GetImageInfoAsync(wim, CancellationToken.None);
    Check(afterCommit.Count == 2, "WIM index count remains stable after commit");

    Directory.CreateDirectory(firstApply);
    Directory.CreateDirectory(secondApply);
    await runner.RunAsync("dism.exe",
        ["/Apply-Image", $"/ImageFile:{wim}", "/Index:1", $"/ApplyDir:{firstApply}", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));
    await runner.RunAsync("dism.exe",
        ["/Apply-Image", $"/ImageFile:{wim}", "/Index:2", $"/ApplyDir:{secondApply}", "/CheckIntegrity"],
        timeout: TimeSpan.FromMinutes(10));
    Check(File.Exists(Path.Combine(firstApply, "committed-by-localizard.txt")),
        "DISM commit persists modifications to WIM index 1");
    Check(File.Exists(Path.Combine(secondApply, "payload-two.txt")),
        "DISM apply restores WIM index 2 without cross-index contamination");

    Console.WriteLine("LocaLizard Windows DISM integration tests passed: capture, append, inspect, mount, commit and apply.");
}
finally
{
    if (mounted)
    {
        try
        {
            await runner.RunAsync("dism.exe", ["/Unmount-Image", $"/MountDir:{mount}", "/Discard"],
                cancellationToken: CancellationToken.None, throwOnError: false, timeout: TimeSpan.FromMinutes(5));
        }
        catch { }
    }
    try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
}
