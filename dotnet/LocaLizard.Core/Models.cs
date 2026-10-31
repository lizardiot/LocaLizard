namespace LocaLizard.Core;

public static class AppInfo
{
    public const string Name = "LocaLizard";
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";
}

public sealed record RunOptions
{
    public required string SourceIso { get; init; }
    public required string OutputIso { get; init; }
    public required string WorkDirectory { get; init; }
    public required string PackageDirectory { get; init; }
    public string? DonorIso { get; init; }
    public string? FodDirectory { get; init; }
    public string? UpdatesDirectory { get; init; }
    public bool KeepWorkFiles { get; init; } = true;
    public bool ProcessSetup { get; init; } = true;
    public bool SplitWim { get; init; }
    public bool RunHyperVTest { get; init; }
    public string? DriverDirectory { get; init; }
    public string? UnattendFile { get; init; }
    public bool AllowPartialLocalization { get; init; }
}

public sealed record ImageInfo(int Index, string Name, string Architecture, string Version)
{
    public int BuildFamily => BuildNumbers.Family(Version);
    public string NormalizedArchitecture => BuildNumbers.Architecture(Architecture);
}

public sealed record UpdatePackage(string File, string Kind, string Sha256, string FullPath);
public sealed record UpdatePlan(int TargetBuildFamily, string Architecture, IReadOnlyList<UpdatePackage> Packages);
public sealed record CapabilityAudit(string Name, string Status, int? DismExitCode = null);
public sealed record ImageAudit(string Image, int Index, string Architecture, string Version, string Package, string Sha256, bool RuRuConfirmed, IReadOnlyList<CapabilityAudit> Capabilities, LocalizationStep? WinRe = null);

public static class BuildNumbers
{
    public static int Family(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        var match = System.Text.RegularExpressions.Regex.Match(version, @"(?:^|\D)10\.0\.(\d+)\.");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var family)) return family;
        match = System.Text.RegularExpressions.Regex.Match(version, @"(?:^|\D)(\d{5})(?:\.\d+)?");
        return match.Success && int.TryParse(match.Groups[1].Value, out family) ? family : 0;
    }

    public static string Architecture(string? value)
    {
        var arch = (value ?? "").Trim().ToLowerInvariant();
        if (arch.Contains("arm64") || arch.Contains("aarch64")) return "arm64";
        if (arch.Contains("x86") && !arch.Contains("64")) return "x86";
        if (arch.Contains("amd64") || arch.Contains("x64") || arch.Contains("x86_64")) return "x64";
        return arch;
    }

    public static bool IsSupportedArchitecture(string architecture) => architecture is "x64" or "x86" or "arm64";
}
