namespace LocaLizard.Core;

/// <summary>Scans a mounted Microsoft Languages and Optional Features ISO as a typed local source.</summary>
public sealed class MicrosoftIsoProvider(string mountedRoot) : ILanguageResourceProvider
{
    private readonly LocalRepositoryProvider _inner = new(mountedRoot, "microsoft-language-optional-features-iso");
    public string SourceId => "microsoft-language-optional-features-iso";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        var results = await _inner.DiscoverAsync(request, cancellationToken);
        return results.Where(x => x.ResourceType == request.ResourceType ||
                                  request.ResourceType is null)
            .Select(x => x with
            {
                Notes = "Local Microsoft Languages and Optional Features ISO source; CBS identity and Microsoft signature must be verified before servicing."
            }).ToArray();
    }
}

/// <summary>Discovers WinPE localization CABs from an extracted ADK WinPE Add-on source.</summary>
public sealed class AdkWinPeProvider(string adkRoot) : ILanguageResourceProvider
{
    private readonly LocalRepositoryProvider _inner = new(adkRoot, "adk-winpe");
    public string SourceId => "adk-winpe";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        var results = await _inner.DiscoverAsync(
            request with { ResourceType = LanguageResourceType.WinPELanguagePack }, cancellationToken);
        return results.Where(x =>
                x.ResourceType == LanguageResourceType.WinPELanguagePack &&
                (x.LocalPath?.Contains($"{Path.DirectorySeparatorChar}WinPE_OCs{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) == true ||
                 x.FileName.Contains("winpe", StringComparison.OrdinalIgnoreCase) ||
                 x.FileName.Equals("lp.cab", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}

/// <summary>Discovers WinRE localization resources from a Windows recovery/ADK source.</summary>
public sealed class AdkWinReProvider(string sourceRoot) : ILanguageResourceProvider
{
    private readonly LocalRepositoryProvider _inner = new(sourceRoot, "adk-winre");
    public string SourceId => "adk-winre";

    public async Task<IReadOnlyList<LanguageResourceCandidate>> DiscoverAsync(
        LanguageResourceRequest request, CancellationToken cancellationToken)
    {
        var results = await _inner.DiscoverAsync(
            request with { ResourceType = LanguageResourceType.WinRELanguagePack }, cancellationToken);
        return results.Where(x =>
                x.ResourceType == LanguageResourceType.WinRELanguagePack &&
                (x.LocalPath?.Contains($"{Path.DirectorySeparatorChar}WinRE{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase) == true ||
                 x.FileName.Contains("winre", StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
