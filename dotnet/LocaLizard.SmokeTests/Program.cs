using LocaLizard.Core;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}

Check(BuildNumbers.Family("10.0.26100.1742") == 26100, "Windows 11 build family");
Check(BuildNumbers.Family("10.0.19044.4046") == 19044, "Windows 10 IoT LTSC build family");
Check(BuildNumbers.Architecture("AMD64") == "x64", "AMD64 normalization");
Check(BuildNumbers.Architecture("ARM64") == "arm64", "ARM64 normalization");
Check(SecurityPolicy.IsTrustedPackageUrl(new Uri("https://download.windowsupdate.com/update.cab")), "trusted Microsoft HTTPS CDN");
Check(SecurityPolicy.IsExactMicrosoftCdnHttp(new Uri("http://tlu.dl.delivery.mp.microsoft.com/filestreamingservice/files/file.cab")), "exact Microsoft HTTP bootstrap path");
Check(!SecurityPolicy.IsTrustedPackageUrl(new Uri("http://example.org/file.cab")), "reject arbitrary HTTP");
Check(!SecurityPolicy.IsTrustedPackageUrl(new Uri("https://download.windowsupdate.com.bad.example/file.cab")), "reject lookalike host");
Check(SecurityPolicy.IsCatalogUrl(new Uri("https://www.catalog.update.microsoft.com/Search.aspx")), "Catalog HTTPS");
Check(!SecurityPolicy.IsCatalogUrl(new Uri("http://www.catalog.update.microsoft.com/Search.aspx")), "Catalog refuses HTTP");

var folder = Path.Combine(Path.GetTempPath(), "LocaLizardTest_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var payload = Path.Combine(folder, "LCU.msu");
    await File.WriteAllTextAsync(payload, "test update payload");
    await using var stream = File.OpenRead(payload);
    var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
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
}
finally
{
    try { Directory.Delete(folder, true); } catch { }
}

Console.WriteLine("LocaLizard C# smoke tests passed.");
