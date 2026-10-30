using System.Text.Json;
using LocaLizard.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

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

var settings = JsonSerializer.Serialize(new AppSettings { SourceIso = "C:\\input.iso", SplitWim = true });
Check(settings.Contains("\\"source_iso\\"") && settings.Contains("\\"split_wim\\""),
    "Python settings keys retained during migration");

var folder = Path.Combine(Path.GetTempPath(), "LocaLizardTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
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
