using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PeerOnQ.DependencyBuilder;

internal static class BuilderSelfTests
{
    internal static async Task<int> RunAsync(CancellationToken token)
    {
        var passed = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Builder self-test assertion failed."); passed++; }
        void Reject(Action action)
        {
            try { action(); }
            catch (InvalidDataException) { passed++; return; }
            throw new InvalidOperationException("Builder self-test expected rejection.");
        }
        const string patch = "--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n old\n-before\n+after\n";
        Check(Encoding.UTF8.GetString(VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(patch), "file.cs")) == "old\nafter\n");
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("wrong\nbefore\n"), Encoding.UTF8.GetBytes(patch), "file.cs"));
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(patch), "other.cs"));
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(patch.Replace("-1,2", "-1,3", StringComparison.Ordinal)), "file.cs"));
        foreach (var path in new[] { "../escape", "folder/../../escape", "/absolute", "C:/absolute", "folder\\escape" })
            Reject(() => VerifiedSources.ContainedPath(Path.GetTempPath(), path));
        var work = Path.Combine(Path.GetTempPath(), "peeronq-builder-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "hash.txt");
        await File.WriteAllTextAsync(input, "verified", token);
        VerifiedSources.RequireHash(input, VerifiedSources.Sha(input)); passed++;
        Reject(() => VerifiedSources.RequireHash(input, new string('0', 64)));
        var linked = Path.Combine(work, "link.tar.gz");
        await using (var output = File.Create(linked))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        using (var writer = new TarWriter(gzip))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "root/link") { LinkName = "../../outside" });
        try { await VerifiedSources.ExtractAsync(linked, Path.Combine(work, "extract"), "root", token); }
        catch (InvalidDataException) { passed++; }
        if (passed != 12) throw new InvalidOperationException("Symlink extraction was not rejected.");
        var manifestPath = Path.Combine(PackageBuilder.FindRepository(), "eng", "sipsorcery", "upstream.json");
        var manifest = JsonSerializer.Deserialize<DependencyManifest>(await File.ReadAllTextAsync(manifestPath, token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var badPackage = Path.Combine(work, "wrong-identity.nupkg");
        using (var zip = ZipFile.Open(badPackage, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("wrong.nuspec").Open()))
            writer.Write("<package><metadata><id>Unexpected.Package</id></metadata></package>");
        Reject(() => PackageBuilder.ValidatePackage(badPackage, manifest, manifestPath));
        PackageBuilder.ValidateManifest(manifest);
        Check(manifest.Patches.Count() == 7);
        Reject(() => PackageBuilder.ValidateManifest(manifest with { AdditionalPatches = [manifest.Patch] }));
        Reject(() => PackageBuilder.ValidateManifest(manifest with { AdditionalPatches = [] }));
        Check(manifest.FinalPatches.Count() == 5
              && manifest.FinalPatches.Single(item => item.TargetPath.EndsWith("/SctpDataSender.cs", StringComparison.Ordinal))
              == manifest.AdditionalPatches![2]
              && manifest.FinalPatches.Single(item => item.TargetPath.EndsWith("/SctpDataReceiver.cs", StringComparison.Ordinal))
              == manifest.AdditionalPatches![3]
              && manifest.FinalPatches.Single(item => item.TargetPath.EndsWith("/UdpReceiver.cs", StringComparison.Ordinal))
              == manifest.AdditionalPatches![4]
              && manifest.FinalPatches.Single(item => item.TargetPath.EndsWith("/ReceptionReport.cs", StringComparison.Ordinal))
              == manifest.AdditionalPatches![5]);
        var disconnected = manifest.AdditionalPatches!.ToArray();
        disconnected[3] = disconnected[3] with { UpstreamFileSha256 = new string('0', 64) };
        Reject(() => PackageBuilder.ValidateManifest(manifest with { AdditionalPatches = disconnected }));
        var disconnectedSender = manifest.AdditionalPatches!.ToArray();
        disconnectedSender[2] = disconnectedSender[2] with { UpstreamFileSha256 = new string('0', 64) };
        Reject(() => PackageBuilder.ValidateManifest(manifest with { AdditionalPatches = disconnectedSender }));
        var reordered = manifest.AdditionalPatches!.ToArray();
        (reordered[^1], reordered[^2]) = (reordered[^2], reordered[^1]);
        Reject(() => PackageBuilder.ValidateManifest(manifest with { AdditionalPatches = reordered }));
        const string secondPatch = "--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n old\n-after\n+final\n";
        var intermediate = VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(patch), "file.cs");
        Check(Encoding.UTF8.GetString(VerifiedSources.ApplyPatch(intermediate, Encoding.UTF8.GetBytes(secondPatch), "file.cs")) == "old\nfinal\n");
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(secondPatch), "file.cs"));
        Console.WriteLine($"Builder self-tests passed: {passed}/22 (patch, context, target, hunk counts, traversal, hash, symlink, package identity, pinned patch set, sender/receiver chain continuity/order and final source selection).");
        return 0;
    }
}
