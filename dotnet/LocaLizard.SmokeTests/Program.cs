using System.Text.Json;
using System.IO.Compression;
using LocaLizard.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

var lifecycleAtEnd = WindowsLifecycle.GetInfo(14393, new DateOnly(2026, 10, 13));
var lifecycleAfterEnd = WindowsLifecycle.GetInfo(14393, new DateOnly(2026, 10, 14));
Check(lifecycleAtEnd is { IsSupported: true }, "IoT Enterprise 2016 is supported through 2026-10-13");
Check(lifecycleAfterEnd is { IsSupported: false }, "IoT Enterprise 2016 is end-of-support from 2026-10-14");
Check(WindowsLifecycle.GetInfo(10240, new DateOnly(2026, 10, 31)) is { IsSupported: false },
    "IoT Enterprise LTSB 2015 lifecycle has ended");
Check(WindowsLifecycle.GetInfo(26100, new DateOnly(2026, 10, 31)) is { IsSupported: true },
    "IoT Enterprise LTSC 2024 remains supported");
Check(WindowsLifecycle.GetInfo(99999) is null, "unknown build has no guessed lifecycle");
Check(AppInfo.Version == "0.7.2.1", "AppInfo version includes the fourth release component");

static IEnumerable<string> SimulatedDeniedDirectoryEntries()
{
    yield return "blocked.partial";
    throw new UnauthorizedAccessException("simulated lazy access denial");
}
static IEnumerable<string> SimulatedDirectories(string directory)
{
    if (directory == "root") return new[] { "blocked", "accessible" };
    if (directory == "blocked") return SimulatedDeniedDirectoryEntries();
    return Array.Empty<string>();
}
var resilientAdkCandidates = AdkBootstrapper.EnumerateFilesSafely(
    "root",
    "oscdimg.exe",
    SimulatedDirectories,
    (directory, _) => directory == "accessible"
        ? new[] { "accessible.oscdimg.exe" }
        : Array.Empty<string>()).ToArray();
Check(resilientAdkCandidates.Contains("accessible.oscdimg.exe", StringComparer.OrdinalIgnoreCase),
    "ADK search continues into accessible sibling folders after lazy UnauthorizedAccessException");

static void Throws<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception("FAILED: " + message);
}

Check(BuildNumbers.Family("10.0.26100.1742") == 26100, "Windows 11 build family");
Check(BuildNumbers.Family("10.0.19044.4046") == 19044, "Windows 10 IoT LTSC build family");
Check(BuildNumbers.Architecture("AMD64") == "x64", "AMD64 normalization");
Check(BuildNumbers.Architecture("ARM64") == "arm64", "ARM64 normalization");
Check(BuildNumbers.Architecture("x86") == "x86", "x86 normalization");
Check(BuildNumbers.Family("10.0.28000.1000") == 28000, "Windows 11 26H1 build family");
Check(PackageCompatibilityService.IsKnownBuildFamily(28000, "Client"), "Windows 11 26H1 is an explicit client family");
Check(W10MuiCatalogProvider.CatalogBuild(28000) == 28000, "W10MUI 28000 catalog mapping");
Check(PackageCompatibilityService.CreateRequest(new ImageInfo(1, "Windows 11 Pro", "x64", "10.0.28000.1000")).BuildFamily == 28000,
    "Windows 11 26H1 client request is accepted");
Throws<InvalidDataException>(() => PackageCompatibilityService.CreateRequest(
    new ImageInfo(1, "Windows 11 IoT Enterprise", "x64", "10.0.28000.1000")),
    "Windows IoT build 28000 is not assumed compatible without validation");
Check(!BuildNumbers.IsSupportedArchitecture("ia64"), "reject unsupported architecture");

Check(SecurityPolicy.IsTrustedPackageUrl(new Uri("https://download.windowsupdate.com/update.cab")), "trusted Microsoft HTTPS CDN");
Check(SecurityPolicy.IsExactMicrosoftCdnHttp(new Uri("http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/file.cab")), "exact Microsoft HTTP bootstrap path");
Check(!SecurityPolicy.IsTrustedPackageUrl(new Uri("http://example.org/file.cab")), "reject arbitrary HTTP");
Check(!SecurityPolicy.IsTrustedPackageUrl(new Uri("https://download.windowsupdate.com.bad.example/file.cab")), "reject lookalike host");
Check(SecurityPolicy.IsCatalogUrl(new Uri("https://www.catalog.update.microsoft.com/Search.aspx")), "Catalog HTTPS");
Check(!SecurityPolicy.IsCatalogUrl(new Uri("http://www.catalog.update.microsoft.com/Search.aspx")), "Catalog refuses HTTP");
Check(!SecurityPolicy.IsCatalogUrl(new Uri("https://www.catalog.update.microsoft.com.evil.example/Search.aspx")), "Catalog rejects lookalike host");
Check(!SecurityPolicy.IsExactMicrosoftCdnHttp(new Uri("http://tlu.dl.delivery.mp.microsoft.com/other/file.cab")), "HTTP CDN requires exact file path");

Check(MicrosoftUpdateCatalogClient.QueryForBuild(26100, "x64").Contains("Windows 11, version 24H2", StringComparison.Ordinal),
    "Catalog build profile");
Check(MicrosoftUpdateCatalogClient.QueryForBuild(19044, "x86").Contains("x86-based Systems", StringComparison.Ordinal),
    "Catalog x86 profile");
Check(MicrosoftUpdateCatalogClient.QueryForBuild(29999, "arm64").Contains("Windows OS Build 29999", StringComparison.Ordinal),
    "Catalog unknown build fallback");
Throws<InvalidDataException>(() => MicrosoftUpdateCatalogClient.QueryForBuild(26100, "ia64"),
    "Catalog rejects unsupported architecture");

var guid = "12345678-1234-1234-1234-1234567890ab";
var catalogHtml = $"""
<table>
<tr>
<td><input type="checkbox" /></td>
<td>2026-10 Cumulative Update for Windows 11, version 24H2 for x64-based Systems (KB5123456) (26100.8123)</td>
<td>Windows 11</td><td>Security Updates</td><td>10/20/2026</td><td></td><td>1.5 GB</td>
<td><a href="javascript:goToDetails('{guid}')">Details</a></td>
</tr>
</table>
""";
var catalogRows = MicrosoftUpdateCatalogClient.ParseSearchResults(catalogHtml);
Check(catalogRows.Count == 1 && catalogRows[0].Id == guid, "Catalog parses result id");
Check(catalogRows[0].BuildFamily == 26100 && catalogRows[0].BuildRevision == 8123, "Catalog parses build/revision");
Check(catalogRows[0].Title.Contains("KB5123456", StringComparison.Ordinal), "Catalog extracts title and KB");
var filteredCatalog = MicrosoftUpdateCatalogClient.FilterResults(catalogRows, "x64", "5123456", 26100);
Check(filteredCatalog.Count == 1, "Catalog stable architecture/build/KB filtering");
Check(MicrosoftUpdateCatalogClient.FilterResults(catalogRows, "arm64", null, 26100).Count == 0,
    "Catalog architecture mismatch rejected");
Check(MicrosoftUpdateCatalogClient.FilterResults(catalogRows, "x64", null, 19044).Count == 0,
    "Catalog build mismatch rejected");
Check(MicrosoftUpdateCatalogClient.FileArchitecture("windows11.0-kb5123456-x64.msu") == "x64",
    "Catalog filename architecture");
Check(MicrosoftUpdateCatalogClient.FileArchitecture("windows11.0-kb5123456_arm64.msu") == "arm64",
    "Catalog filename underscore architecture");

var sha1 = new string('a', 40);
var catalogDownloadHtml = $"""
<script>
updateFiles[0].url = 'https://catalog.s.download.windowsupdate.com/cab/file.msu';
updateFiles[0].fileName = 'windows11.0-kb5123456-x64.msu';
updateFiles[0].digest = 'sha1:{sha1}';
updateFiles[0].size = 123456;
</script>
""";
var catalogFiles = MicrosoftUpdateCatalogClient.ExtractDownloads(catalogDownloadHtml);
Check(catalogFiles.Count == 1, "Catalog extracts download link");
Check(catalogFiles[0].Sha1 == sha1 && catalogFiles[0].Size == 123456, "Catalog extracts SHA-1 and file size");
Check(catalogFiles[0].Url.Scheme == "https", "Catalog download requires HTTPS");
var evilCatalogHtml = """
<script>
updateFiles[0].url = 'https://evil.example/file.msu';
updateFiles[0].fileName = 'windows11.0-kb5123456-x64.msu';
</script>
""";
Check(MicrosoftUpdateCatalogClient.ExtractDownloads(evilCatalogHtml).Count == 0,
    "Catalog rejects untrusted download hosts");

using (var uupBuildsDoc = JsonDocument.Parse("""
{
  "response": {
    "builds": {
      "0": { "uuid": "first", "title": "Windows 11, version 24H2", "build": "10.0.26100.1", "arch": "amd64" },
      "2": { "uuid": "third", "title": "Windows 11, version 24H2", "build": "10.0.26100.2", "arch": "amd64" }
    }
  }
}
"""))
{
    var rows = UupDumpClient.GetBuilds(uupBuildsDoc.RootElement).ToArray();
    Check(rows.Length == 2 && rows[1].GetProperty("uuid").GetString() == "third",
        "UUP Dump sparse object build results");
    Check(UupDumpClient.CandidateMatches(rows[0], 26100, "x64"),
        "UUP Dump client build selection");
    Check(!UupDumpClient.CandidateMatches(rows[0], 19044, "x64"),
        "UUP Dump exact build family match");
}
var manyBuilds = Enumerable.Range(0, 40).Select(i => new
{
    uuid = $"id-{i}",
    title = "Windows 11, version 24H2",
    build = $"10.0.26100.{i + 1}",
    arch = "amd64"
}).ToArray();
using (var manyBuildsDoc = JsonDocument.Parse(JsonSerializer.Serialize(new { response = new { builds = manyBuilds } })))
    Check(UupDumpClient.GetClientBuildCandidates(manyBuildsDoc.RootElement, 26100, "x64").Count == 40,
        "UUP Dump candidate discovery does not truncate compatible builds after the first 32");

using (var uupNoResultsDoc = JsonDocument.Parse("""{"response":{"error":"SEARCH_NO_RESULTS"}}"""))
    Check(!UupDumpClient.GetBuilds(uupNoResultsDoc.RootElement).Any(),
        "UUP Dump SEARCH_NO_RESULTS is treated as an empty result");
using (var uupBadBuildDoc = JsonDocument.Parse("""{"response":{"builds":{"0":"26100"}}}"""))
    Throws<InvalidDataException>(() => UupDumpClient.GetBuilds(uupBadBuildDoc.RootElement).ToArray(),
        "UUP Dump rejects malformed sparse build entries");
using (var uupLangDoc = JsonDocument.Parse("""{"response":{"langList":{"0":"ru-RU","1":"en-US"}}}"""))
    Check(UupDumpClient.GetLanguages(uupLangDoc.RootElement).Contains("ru-ru"),
        "UUP Dump parses language object entries");
using (var uupBadLangDoc = JsonDocument.Parse("""{"response":{"languages":["ru-RU"]}}"""))
    Throws<InvalidDataException>(() => UupDumpClient.GetLanguages(uupBadLangDoc.RootElement),
        "UUP Dump requires langList");
using (var uupFilesDoc = JsonDocument.Parse("""
{
  "response": {
    "files": {
      "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab": {
        "url": "https://download.microsoft.com/client-language-pack.cab",
        "sha1": "0123456789012345678901234567890123456789",
        "size": 1024
      },
      "bad-entry.cab": "unexpected-string"
    }
  }
}
"""))
{
    var files = UupDumpClient.GetFiles(uupFilesDoc.RootElement);
    Check(files.Count == 1 && files[0].Name.StartsWith("Microsoft-Windows-Client", StringComparison.Ordinal),
        "UUP Dump ignores malformed string file metadata");
    Check(UupDumpClient.IsClientLp(files[0], "x64"), "UUP Dump recognizes Client Language Pack CAB");
    Check(!UupDumpClient.IsClientLp(files[0], "arm64"), "UUP Dump rejects wrong-architecture Client Language Pack CAB");
}
var originalUupPackage = new UupDumpClient.UupFile(
    "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab",
    new Uri("https://download.microsoft.com/old.cab"), new string('a', 40), 1024);
var refreshedUupPackage = new UupDumpClient.UupFile(
    originalUupPackage.Name, new Uri("https://download.microsoft.com/new.cab"), new string('a', 40), 1024);
var changedUupPackage = new UupDumpClient.UupFile(
    originalUupPackage.Name, new Uri("https://download.microsoft.com/new.cab"), new string('b', 40), 1024);
Check(UupDumpClient.SamePackageMetadata(originalUupPackage, refreshedUupPackage),
    "UUP temporary link refresh preserves Russian package identity and digest");
Check(!UupDumpClient.SamePackageMetadata(originalUupPackage, changedUupPackage),
    "UUP temporary link refresh rejects changed package digest");

var uupEsd = new UupDumpClient.UupFile(
    "Microsoft-Windows-Client-LanguagePack-Package_ru-ru~31bf3856ad364e35~amd64~ru-ru~.esd",
    new Uri("https://download.microsoft.com/client-language-pack.esd"), new string('a', 40), 2048);
Check(UupDumpClient.IsClientLp(uupEsd, "x64"), "UUP Dump recognizes Client Language Pack ESD");
Check(!UupDumpClient.IsClientLp(uupEsd, "arm64"), "UUP Dump rejects wrong-architecture Client Language Pack ESD");
Check(WindowsImageService.IsClientLp(
    @"C:\cache\auto\26100_x64\Microsoft-Windows-Client-LanguagePack-Package_ru-ru~amd64~_extracted\update.mum",
    "x64"), "Client Language Pack update.mum inherits architecture from package identity");
Check(!WindowsImageService.IsClientLp(
    @"C:\cache\auto\26100_x64\Microsoft-Windows-Client-LanguagePack-Package_ru-ru~arm64~_extracted\update.mum",
    "x64"), "Client Language Pack update.mum rejects wrong architecture");
var uupServer = JsonDocument.Parse("""{"title":"Update for Microsoft server operating system (26100.33674)","build":"10.0.26100.33674","arch":"amd64"}""");
Check(!UupDumpClient.CandidateMatches(uupServer.RootElement, 26100, "x64"),
    "UUP Dump rejects server packages");
uupServer.Dispose();

var settings = JsonSerializer.Serialize(new AppSettings { SourceIso = "C:\\input.iso", SplitWim = true });
Check(settings.Contains("source_iso", StringComparison.Ordinal) && settings.Contains("split_wim", StringComparison.Ordinal),
    "Python settings keys retained during migration");

var folder = Path.Combine(Path.GetTempPath(), "LocaLizardTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var managedRepositoryRoot = Path.Combine(folder, "managed-resource-repository");
    Directory.CreateDirectory(managedRepositoryRoot);
    var managedResourcePath = Path.Combine(managedRepositoryRoot, "client-language-pack_ru-ru_x64.cab");
    await File.WriteAllTextAsync(managedResourcePath, "signed fixture bytes");
    var managedCandidate = new LanguageResourceCandidate(
        "fixture:managed-resource", "test-provider", Path.GetFileName(managedResourcePath),
        LanguageResourceType.ClientLanguagePack, "ru-RU", "x64", 26100, "Client",
        Path.GetFileName(managedResourcePath), LocalPath: managedResourcePath,
        TrustLevel: ResourceTrustLevel.MicrosoftSigned);
    var testSignature = new PackageSignatureResult(true, "valid", "CN=Microsoft Corporation", "TESTTHUMBPRINT");
    var managedRepository = new ManagedRepositoryService();
    await managedRepository.RegisterAsync(managedRepositoryRoot, managedResourcePath,
        managedCandidate, testSignature, CancellationToken.None);
    Check(await managedRepository.VerifyAsync(managedRepositoryRoot, managedResourcePath, CancellationToken.None),
        "managed repository verifies Russian package provenance and SHA-256");
    await File.WriteAllTextAsync(managedResourcePath, "tampered bytes");
    Check(!await managedRepository.VerifyAsync(managedRepositoryRoot, managedResourcePath, CancellationToken.None),
        "managed repository rejects modified package bytes");

var ruRequest = new LanguageResourceRequest(22631, "x64", "ru-RU", "Client",
    LanguageResourceType.ClientLanguagePack);
var compatibleRuCandidate = new LanguageResourceCandidate(
    "test:ru", "test", "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab",
    LanguageResourceType.ClientLanguagePack, "ru-RU", "x64", 22631, "Client",
    "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab",
    LocalPath: @"C:\cache\ru.cab");
Check(PackageCompatibilityService.IsCompatible(compatibleRuCandidate, ruRequest),
    "typed resource compatibility accepts the requested Russian client LP");
Check(PackageCompatibilityService.IsCompatiblePackageBuild(22631, 22621),
    "Windows 11 23H2 accepts its explicitly shared 22621 language package family");
Check(!PackageCompatibilityService.IsCompatiblePackageBuild(26100, 22621),
    "Windows 11 24H2 does not inherit a mismatched 22621 package");
Check(!PackageCompatibilityService.IsCompatible(compatibleRuCandidate with { Language = "en-US" }, ruRequest),
    "typed resource compatibility rejects non-Russian candidates");
Check(!PackageCompatibilityService.IsCompatible(compatibleRuCandidate with { Architecture = "arm64" }, ruRequest),
    "typed resource compatibility rejects architecture mismatch");
Check(PackageCompatibilityService.IsKnownBuildFamily(20348, "Server") &&
      PackageCompatibilityService.IsKnownBuildFamily(26100, "Server"),
    "Windows Server 2022 and 2025 have explicit compatibility families");

var w10MuiLpCatalog = """
Client x64
ru-RU
https://download.microsoft.com/download/test/Microsoft-Windows-Client-LanguagePack-Package_ru-ru~31bf3856ad364e35~amd64~ru-ru~10.0.22621.1.esd
en-US
https://download.microsoft.com/download/test/not-russian.esd
""";
var w10MuiCandidates = W10MuiCatalogProvider.ParseCatalog(w10MuiLpCatalog, ruRequest, 22621);
Check(w10MuiCandidates.Count == 1 &&
      w10MuiCandidates[0].Language == "ru-RU" &&
      w10MuiCandidates[0].Architecture == "x64" &&
      w10MuiCandidates[0].ResourceType == LanguageResourceType.ClientLanguagePack,
    "W10MUI parser selects the Russian x64 client language pack and ignores other locales");
var w10MuiFodRequest = ruRequest with { ResourceType = LanguageResourceType.FeatureOnDemand };
var w10MuiFodCatalog = """
Client x64
ru-RU
https://download.microsoft.com/download/test/Microsoft-Windows-LanguageFeatures-Basic-ru-ru-Package~31bf3856ad364e35~amd64~~10.0.22621.1.cab
ru-RU
https://download.microsoft.com/download/test/Microsoft-Windows-WinOcr-FOD-Package~31bf3856ad364e35~amd64~~10.0.22621.1.cab
""";
var w10MuiFodCandidates = W10MuiCatalogProvider.ParseCatalog(
    w10MuiFodCatalog, w10MuiFodRequest, 22621, featureOnDemand: true);
Check(w10MuiFodCandidates.Count == 2 &&
      w10MuiFodCandidates.All(x => x.ResourceType == LanguageResourceType.FeatureOnDemand),
    "W10MUI parser resolves only Russian FOD CAB candidates");

var dexitHashX86 = new string('a', 40);
var dexitHashX64 = new string('b', 40);
var dexitCatalog = $"""
 | LangPacks for Windows 10 build 10.0.14393.0 (rs1_release)
 | x86
 | ru-RU http://download.windowsupdate.com/c/msdownload/update/software/updt/2016/07/lp_{{dexitHashX86}}.cab
 | en-US http://download.windowsupdate.com/c/msdownload/update/software/updt/2016/07/lp_{{new string('c', 40)}}.cab
 | x64
 | ru-RU http://download.windowsupdate.com/c/msdownload/update/software/updt/2016/07/lp_{{dexitHashX64}}.cab
""";
var dexitX64 = DexitGistProvider.ParseCatalog(dexitCatalog,
    new LanguageResourceRequest(14393, "x64", "ru-RU", "Client", LanguageResourceType.ClientLanguagePack));
Check(dexitX64.Count == 1 && dexitX64[0].SourceId == "dexit-gist" &&
      dexitX64[0].Architecture == "x64" && dexitX64[0].Sha1 == dexitHashX64 &&
      dexitX64[0].DownloadUri?.Scheme == "https" &&
      dexitX64[0].TrustLevel == ResourceTrustLevel.ThirdPartyMetadataMicrosoftFile,
    "Dexit parser selects only the Russian x64 RS1 Microsoft CDN CAB and normalizes it to HTTPS");
var dexitX86 = DexitGistProvider.ParseCatalog(dexitCatalog,
    new LanguageResourceRequest(14393, "x86", "ru-RU", "Client", LanguageResourceType.ClientLanguagePack));
Check(dexitX86.Count == 1 && dexitX86[0].Architecture == "x86",
    "Dexit parser selects the Russian x86 RS1 CAB from its architecture section");
Check(DexitGistProvider.ParseCatalog(dexitCatalog,
    new LanguageResourceRequest(17763, "x64", "ru-RU", "Client", LanguageResourceType.ClientLanguagePack)).Count == 0,
    "Dexit RS1 metadata is not reused for another Windows build family");
Check(SecurityPolicy.IsTrustedMetadataUrl(new Uri(DexitGistProvider.CatalogUrl)),
    "pinned Dexit Gist metadata endpoint is allow-listed");
Check(!SecurityPolicy.IsTrustedMetadataUrl(new Uri("https://api.github.com/gists/87f30cffa70ea6899177f536a8dc788f")),
    "unversioned or unrelated GitHub Gist API URLs are not allow-listed");

var osBuilderCatalog = """
<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04">
  <Obj RefId="0">
    <MS>
      <S N="Category">LanguagePack</S>
      <S N="KBTitle">Russian Language Pack Windows 10 Version 1809 for x64-based Systems</S>
      <S N="FileName">Microsoft-Windows-Client-LanguagePack-Package_ru-ru~amd64~10.0.17763.1.cab</S>
      <S N="URL">https://download.windowsupdate.com/c/msdownload/update/software/updt/2026/01/ru-lp.cab</S>
    </MS>
  </Obj>
</Objs>
""";
var osBuilderCandidates = OsBuilderCatalogProvider.ParseCatalog(osBuilderCatalog,
    new LanguageResourceRequest(17763, "x64", "ru-RU", "Client", LanguageResourceType.ClientLanguagePack));
Check(osBuilderCandidates.Count == 1 &&
      osBuilderCandidates[0].SourceId == "osbuilder-public" &&
      osBuilderCandidates[0].BuildFamily == 17763,
    "OSBuilder CLIXML parser extracts the Russian 1809 x64 language pack");

Check(SecurityPolicy.IsTrustedMetadataUrl(
        new Uri("https://raw.githubusercontent.com/abbodi1406/BatUtil/master/W10MUI/Langs/22621-LPs.txt")),
    "W10MUI metadata URL is on the dedicated allow-list");
Check(!SecurityPolicy.IsTrustedMetadataUrl(
        new Uri("https://raw.githubusercontent.com/attacker/BatUtil/master/W10MUI/Langs/22621-LPs.txt")),
    "W10MUI metadata lookalike path is rejected");
Check(FodCapabilityCatalog.RussianCapabilities.All(x => x.Contains("~~~ru-RU~", StringComparison.Ordinal)),
    "FOD capability catalog contains only Russian capability identities");
Check(FodCapabilityCatalog.RussianCapabilities.ToList().IndexOf("Language.TextToSpeech~~~ru-RU~0.0.1.0") <
      FodCapabilityCatalog.RussianCapabilities.ToList().IndexOf("Language.Speech~~~ru-RU~0.0.1.0"),
    "Russian text-to-speech is ordered before speech recognition");

var fodInventory = FodInventoryService.ParseCapabilities("""
Capability Identity : Language.Basic~~~ru-RU~0.0.1.0
State : Not Present
Capability Identity : Language.OCR~~~ru-RU~0.0.1.0
State : Installed
""");
Check(fodInventory.Count == 2 && fodInventory[0].State == "Not Present" &&
      fodInventory[1].State == "Installed",
    "DISM capability inventory parses identities and install states");

var lxpRoot = Path.Combine(folder, "lxp-fixture");
Directory.CreateDirectory(lxpRoot);
var lxpPath = Path.Combine(lxpRoot, "Microsoft.LanguageExperiencePackru-RU_1.0.0.0_x64.msix");
using (var archive = ZipFile.Open(lxpPath, ZipArchiveMode.Create))
{
    var manifestEntry = archive.CreateEntry("AppxManifest.xml");
    await using var manifestStream = manifestEntry.Open();
    await using var writer = new StreamWriter(manifestStream);
    await writer.WriteAsync("""
        <Package>
          <Identity Name="Microsoft.LanguageExperiencePackru-RU"
                    Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"
                    Version="1.0.0.0" ProcessorArchitecture="x64" />
          <Properties />
          <Resources><Resource Language="ru-RU" /></Resources>
          <Dependencies>
            <PackageDependency Name="Microsoft.VCLibs.140.00"
                               Publisher="CN=Microsoft Corporation"
                               MinVersion="14.0.0.0" />
          </Dependencies>
        </Package>
        """);
}
var lxpInfo = new AppxPackageInspector().Inspect(lxpPath);
Check(AppxPackageInspector.IsRussianMicrosoftLxp(lxpInfo, "x64"),
    "AppxPackageInspector recognizes a Microsoft ru-RU x64 LXP manifest");
Check(!AppxPackageInspector.IsRussianMicrosoftLxp(lxpInfo, "arm64"),
    "AppxPackageInspector rejects an architecture mismatch");
var dependencyPath = Path.Combine(lxpRoot, "Microsoft.VCLibs.140.00_14.0.0.0_x64.msix");
using (var archive = ZipFile.Open(dependencyPath, ZipArchiveMode.Create))
{
    var manifestEntry = archive.CreateEntry("AppxManifest.xml");
    await using var manifestStream = manifestEntry.Open();
    await using var writer = new StreamWriter(manifestStream);
    await writer.WriteAsync("""
        <Package>
          <Identity Name="Microsoft.VCLibs.140.00"
                    Publisher="CN=Microsoft Corporation"
                    Version="14.0.0.0" ProcessorArchitecture="x64" />
          <Resources><Resource Language="en-US" /></Resources>
        </Package>
        """);
}
var dependencyResolution = new AppxDependencyResolver().Resolve(lxpInfo, [dependencyPath], "x64");
Check(dependencyResolution.IsComplete && dependencyResolution.DependencyPaths.Count == 1,
    "Appx dependency resolver selects a matching Microsoft framework package");
var missingDependencyResolution = new AppxDependencyResolver().Resolve(lxpInfo, [], "x64");
Check(!missingDependencyResolution.IsComplete &&
      missingDependencyResolution.MissingDependencies.Contains("Microsoft.VCLibs.140.00"),
    "Appx dependency resolver reports missing prerequisites instead of provisioning blindly");

var providerRoot = Path.Combine(folder, "provider-fixtures");
var winPeRu = Path.Combine(providerRoot, "WinPE_OCs", "amd64", "ru-ru");
Directory.CreateDirectory(winPeRu);
await File.WriteAllTextAsync(Path.Combine(winPeRu, "lp.cab"), "fixture");
var winPeRequest = new LanguageResourceRequest(26100, "x64", "ru-RU", "Client",
    LanguageResourceType.WinPELanguagePack);
var winPeCandidates = await new AdkWinPeProvider(providerRoot).DiscoverAsync(winPeRequest, CancellationToken.None);
Check(winPeCandidates.Count == 1 &&
      winPeCandidates[0].ResourceType == LanguageResourceType.WinPELanguagePack,
    "ADK WinPE provider discovers a Russian lp.cab in the architecture-specific WinPE_OCs tree");
var winReRu = Path.Combine(providerRoot, "WinRE", "ru-ru");
Directory.CreateDirectory(winReRu);
await File.WriteAllTextAsync(Path.Combine(winReRu, "lp.cab"), "fixture");
var winReRequest = winPeRequest with { ResourceType = LanguageResourceType.WinRELanguagePack };
var winReCandidates = await new AdkWinReProvider(providerRoot).DiscoverAsync(winReRequest, CancellationToken.None);
Check(winReCandidates.Count == 1 &&
      winReCandidates[0].ResourceType == LanguageResourceType.WinRELanguagePack,
    "ADK WinRE provider discovers Russian resources separately from WinPE");

var microsoftIsoRoot = Path.Combine(providerRoot, "MicrosoftISO", "LanguagesAndOptionalFeatures", "26100_x64", "ru-ru");
Directory.CreateDirectory(microsoftIsoRoot);
await File.WriteAllTextAsync(Path.Combine(microsoftIsoRoot,
    "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab"), "fixture");
var isoCandidates = await new MicrosoftIsoProvider(Path.Combine(providerRoot, "MicrosoftISO"))
    .DiscoverAsync(new LanguageResourceRequest(26100, "x64", "ru-RU", "Client",
        LanguageResourceType.ClientLanguagePack), CancellationToken.None);
Check(isoCandidates.Count == 1 && isoCandidates[0].SourceId == "microsoft-language-optional-features-iso",
    "Microsoft ISO provider discovers local Russian language resources");

var optionalRoot = Path.Combine(folder, "optional-component-fixtures");
var optionalRu = Path.Combine(optionalRoot, "ru-ru");
Directory.CreateDirectory(optionalRu);
var optionalNotepad = Path.Combine(optionalRu,
    "Microsoft-Windows-Notepad-FOD-Package_ru-ru~amd64~10.0.26100.1.cab");
await File.WriteAllTextAsync(optionalNotepad, "fixture");
var optionalCandidates = await new LocalRepositoryProvider(optionalRoot).DiscoverAsync(
    new LanguageResourceRequest(26100, "x64", "ru-RU", "Client"), CancellationToken.None);
Check(optionalCandidates.Count == 1 &&
      optionalCandidates[0].ResourceType == LanguageResourceType.LocalizedOptionalComponent,
    "localized optional components are inventoried as a separate type");
Check(!PackageCompatibilityService.IsCompatible(optionalCandidates[0],
      new LanguageResourceRequest(26100, "x64", "ru-RU", "Client", LanguageResourceType.FeatureOnDemand)),
    "localized optional components are not passed into the core language FoD flow");

var sourceSettingsPath = Path.Combine(folder, "language-sources.json");
var sourceSettings = new LanguageSourceConfiguration();
sourceSettings.SetEnabled("dexit-gist", false);
sourceSettings.Save(sourceSettingsPath);
var reloadedSourceSettings = LanguageSourceConfiguration.Load(sourceSettingsPath);
Check(!reloadedSourceSettings.IsEnabled("dexit-gist") && reloadedSourceSettings.IsEnabled("w10mui") &&
      reloadedSourceSettings.EnabledAutomaticSourceCount == 2,
    "automatic source settings persist in a local versioned JSON configuration");
using (var defaultSourceRegistry = LanguageSourceFactory.CreateDefault(reloadedSourceSettings))
    Check(defaultSourceRegistry.Sources.SequenceEqual(new[] { "osbuilder-public", "w10mui" }),
        "central source factory registers enabled providers only");
Check(LanguageSourceCatalog.ManualSources.Any(x => x.SourceId == "community-import") &&
      LanguageSourceCatalog.ReferenceSources.Any(x => x.SourceId == "m417z-language-files"),
    "manual community imports and extracted reference files are separated from automatic providers");

    var payload = Path.Combine(folder, "LCU.msu");
    await File.WriteAllTextAsync(payload, "test update payload");
    string hash;
    await using (var stream = File.OpenRead(payload))
        hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
    var manifest = $$"""
    {
      "schema_version": 1,
      "target_build_family": 26100,
      "architecture": "x64",
      "ordered_packages": [
        { "file": "LCU.msu", "kind": "target_lcu", "sha256": "{{hash}}" }
      ]
    }
    """;
    await File.WriteAllTextAsync(Path.Combine(folder, "updates.json"), manifest);
    var plan = await UpdateManifestValidator.LoadAsync(folder);
    Check(plan.Packages.Count == 1 && plan.Packages[0].Kind == "target_lcu", "valid update manifest");

    var wrongBuildRejected = false;
    try { await UpdateManifestValidator.LoadAsync(folder, new ImageInfo(1, "Windows", "x64", "10.0.19044.1")); }
    catch (InvalidDataException) { wrongBuildRejected = true; }
    Check(wrongBuildRejected, "reject wrong build");

    await File.WriteAllTextAsync(payload, "modified update payload");
    var tamperRejected = false;
    try { await UpdateManifestValidator.LoadAsync(folder); }
    catch (InvalidDataException) { tamperRejected = true; }
    Check(tamperRejected, "reject modified update payload");

    var autoCache = Path.Combine(folder, "cache", "auto", "26100_x64");
    Directory.CreateDirectory(autoCache);
    var cachedPackage = Path.Combine(autoCache, "client-language-pack_ru-ru_x64.cab");
    await File.WriteAllTextAsync(cachedPackage, "verified cache content");
    var cachedInfo = new FileInfo(cachedPackage);
    string cachedSha256;
    await using (var cachedStream = File.OpenRead(cachedPackage))
        cachedSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(cachedStream)).ToLowerInvariant();
    string cachedSha1;
    await using (var cachedStream = File.OpenRead(cachedPackage))
        cachedSha1 = Convert.ToHexString(await System.Security.Cryptography.SHA1.HashDataAsync(cachedStream)).ToLowerInvariant();
    var cacheManifest = new
    {
        schema_version = 1,
        packages = new Dictionary<string, object>
        {
            ["client-language-pack_ru-ru_x64.cab"] = new
            {
                file = "client-language-pack_ru-ru_x64.cab", size = cachedInfo.Length,
                sha1 = cachedSha1, sha256 = cachedSha256, api_sha1 = cachedSha1,
                source_host = "download.microsoft.com", downloaded_at = DateTimeOffset.UtcNow
            }
        }
    };
    await File.WriteAllTextAsync(Path.Combine(autoCache, "package_manifest.json"),
        JsonSerializer.Serialize(cacheManifest));
    Check(await UupDumpClient.VerifyManagedCacheFileAsync(cachedPackage, CancellationToken.None),
        "verify legacy-compatible managed package cache manifest");

    var schema2Manifest = new
    {
        schema_version = 2,
        packages = new Dictionary<string, object>
        {
            ["client-language-pack_ru-ru_x64.cab"] = new
            {
                file = "client-language-pack_ru-ru_x64.cab", size = cachedInfo.Length,
                sha1 = cachedSha1, sha256 = cachedSha256, api_sha1 = cachedSha1,
                source_host = "download.microsoft.com", downloaded_at = DateTimeOffset.UtcNow,
                source_package = "Microsoft-Windows-Client-LanguagePack-Package_ru-ru_x64.cab",
                resource_type = "ClientLanguagePack", language = "ru-RU",
                architecture = "x64", target_build_family = 26100
            }
        }
    };
    await File.WriteAllTextAsync(Path.Combine(autoCache, "package_manifest.json"),
        JsonSerializer.Serialize(schema2Manifest));
    Check(await UupDumpClient.VerifyManagedCacheFileAsync(cachedPackage, CancellationToken.None),
        "verify schema 2 Russian package provenance metadata");
    var wrongLanguageSchema2 = new
    {
        schema_version = 2,
        packages = new Dictionary<string, object>
        {
            ["client-language-pack_ru-ru_x64.cab"] = new
            {
                file = "client-language-pack_ru-ru_x64.cab", size = cachedInfo.Length,
                sha1 = cachedSha1, sha256 = cachedSha256, api_sha1 = cachedSha1,
                source_host = "download.microsoft.com", downloaded_at = DateTimeOffset.UtcNow,
                source_package = "unexpected-package.cab", resource_type = "ClientLanguagePack",
                language = "en-US", architecture = "x64", target_build_family = 26100
            }
        }
    };
    await File.WriteAllTextAsync(Path.Combine(autoCache, "package_manifest.json"),
        JsonSerializer.Serialize(wrongLanguageSchema2));
    Check(!await UupDumpClient.VerifyManagedCacheFileAsync(cachedPackage, CancellationToken.None),
        "reject schema 2 cache record for a language other than ru-RU");
    await File.WriteAllTextAsync(Path.Combine(autoCache, "package_manifest.json"),
        JsonSerializer.Serialize(schema2Manifest));
    await File.WriteAllTextAsync(cachedPackage, "tampered cache content");
    Check(!await UupDumpClient.VerifyManagedCacheFileAsync(cachedPackage, CancellationToken.None),
        "reject tampered managed cache package");

    // Exercise the legacy manifest contract beyond the happy path: checkpoints must
    // precede exactly one target LCU, and paths/types/duplicates must be rejected.
    var checkpointPath = Path.Combine(folder, "checkpoint.cab");
    await File.WriteAllTextAsync(checkpointPath, "checkpoint payload");
    string checkpointHash;
    await using (var checkpointStream = File.OpenRead(checkpointPath))
        checkpointHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(checkpointStream)).ToLowerInvariant();
    string lcuHash;
    await using (var lcuStream = File.OpenRead(payload))
        lcuHash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(lcuStream)).ToLowerInvariant();
    async Task<bool> RejectManifestAsync(object value)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "updates.json"), JsonSerializer.Serialize(value));
        try { await UpdateManifestValidator.LoadAsync(folder); return false; }
        catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException or JsonException)
        { return true; }
    }
    var orderedManifest = new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "checkpoint.cab", kind = "checkpoint", sha256 = checkpointHash },
            new { file = "LCU.msu", kind = "target_lcu", sha256 = lcuHash }
        }
    };
    await File.WriteAllTextAsync(Path.Combine(folder, "updates.json"), JsonSerializer.Serialize(orderedManifest));
    var orderedPlan = await UpdateManifestValidator.LoadAsync(folder);
    Check(orderedPlan.Packages.Count == 2 && orderedPlan.Packages[0].Kind == "checkpoint" &&
          orderedPlan.Packages[1].Kind == "target_lcu", "checkpoint precedes target LCU");

    Check(await RejectManifestAsync(new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "LCU.msu", kind = "target_lcu", sha256 = lcuHash },
            new { file = "checkpoint.cab", kind = "checkpoint", sha256 = checkpointHash }
        }
    }), "reject checkpoint after target LCU");
    Check(await RejectManifestAsync(new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "LCU.msu", kind = "target_lcu", sha256 = lcuHash },
            new { file = "LCU.msu", kind = "target_lcu", sha256 = lcuHash }
        }
    }), "reject duplicate update package");
    Check(await RejectManifestAsync(new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "../LCU.msu", kind = "target_lcu", sha256 = lcuHash }
        }
    }), "reject update path traversal");
    Check(await RejectManifestAsync(new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "LCU.msu", kind = "unknown_kind", sha256 = lcuHash }
        }
    }), "reject unknown update kind");
    Check(await RejectManifestAsync(new
    {
        schema_version = 1, target_build_family = 26100, architecture = "x64",
        ordered_packages = new object[]
        {
            new { file = "LCU.msu", kind = "target_lcu", sha256 = lcuHash },
            new { file = "checkpoint.cab", kind = "target_lcu", sha256 = checkpointHash }
        }
    }), "reject multiple target LCUs");

    await File.WriteAllTextAsync(Path.Combine(folder, "install.swm"), "first");
    await File.WriteAllTextAsync(Path.Combine(folder, "install2.swm"), "second");
    await File.WriteAllTextAsync(Path.Combine(folder, "install3.swm"), "third");
    var swm = WimSplitService.Sequence(folder);
    Check(swm.Count == 3 && Path.GetFileName(swm[1]) == "install2.swm" &&
          Path.GetFileName(swm[2]) == "install3.swm", "split-WIM sequencing");
    File.Delete(Path.Combine(folder, "install2.swm"));
    Throws<InvalidDataException>(() => WimSplitService.Sequence(folder), "split-WIM gap rejected");
}
finally
{
    try { Directory.Delete(folder, true); } catch { }
}

Console.WriteLine("LocaLizard C# regression smoke tests passed.");
