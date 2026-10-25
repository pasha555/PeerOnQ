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
        // CRLF sources: the carriage return is part of each line and must match exactly.
        const string crlfPatch = "--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n old\r\n-before\r\n+after\r\n";
        Check(Encoding.UTF8.GetString(VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\r\nbefore\r\n"), Encoding.UTF8.GetBytes(crlfPatch), "file.cs")) == "old\r\nafter\r\n");
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(crlfPatch), "file.cs"));
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
        if (passed != 14) throw new InvalidOperationException("Symlink extraction was not rejected.");

        var manifestPath = Path.Combine(PackageBuilder.FindRepository(), "eng", "sipsorcery", "upstream.json");
        var manifest = JsonSerializer.Deserialize<DependencyManifest>(await File.ReadAllTextAsync(manifestPath, token), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var badPackage = Path.Combine(work, "wrong-identity.nupkg");
        using (var zip = ZipFile.Open(badPackage, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("wrong.nuspec").Open()))
            writer.Write("<package><metadata><id>Unexpected.Package</id></metadata></package>");
        Reject(() => PackageBuilder.ValidatePackage(badPackage, manifest, manifestPath));

        // The pinned manifest is exactly the reviewed change set.
        PackageBuilder.ValidateManifest(manifest);
        Check(manifest.Patches.Length == PackageBuilder.ReviewedPatches.Length
              && manifest.Added.Count == PackageBuilder.ReviewedAddedFiles.Length);
        Check(manifest.FinalPatches.Count() == manifest.Patches.Select(item => item.TargetPath).Distinct(StringComparer.Ordinal).Count());
        Reject(() => PackageBuilder.ValidateManifest(manifest with { Patches = [] }));
        Reject(() => PackageBuilder.ValidateManifest(manifest with { Patches = [.. manifest.Patches, manifest.Patches[0] with { File = "unreviewed-change.patch" }] }));
        if (manifest.Patches.Length > 1)
            Reject(() => PackageBuilder.ValidateManifest(manifest with { Patches = [.. manifest.Patches.Skip(1), manifest.Patches[0]] }));
        Reject(() => PackageBuilder.ValidateManifest(manifest with { SchemaVersion = 1 }));
        Reject(() => PackageBuilder.ValidateManifest(manifest with
        {
            MitigatedAdvisories = [new MitigatedAdvisory("GHSA-2222-3333-4444", "Unknown fix", ["missing.patch"])]
        }));

        // Chain rules, independent of the pinned list: a second patch on a file continues from the
        // reviewed output of the first; added files never replace a patched file.
        var first = new PatchIdentity("first.patch", new string('1', 64), "src/net/File.cs", new string('a', 64), new string('b', 64));
        var second = new PatchIdentity("second.patch", new string('2', 64), "src/net/File.cs", new string('b', 64), new string('c', 64));
        (string, string)[] reviewedChain = [("first.patch", "src/net/File.cs"), ("second.patch", "src/net/File.cs")];
        var chained = manifest with { Patches = [first, second], AddedFiles = null, MitigatedAdvisories = null };
        PackageBuilder.ValidateManifest(chained, reviewedChain, []); passed++;
        Check(chained.FinalPatches.Single() == second);
        Reject(() => PackageBuilder.ValidateManifest(chained with { Patches = [first, second with { UpstreamFileSha256 = new string('0', 64) }] }, reviewedChain, []));
        Reject(() => PackageBuilder.ValidateManifest(chained with { Patches = [second, first] }, reviewedChain, []));
        var colliding = chained with { AddedFiles = [new AddedFileIdentity("File.cs", new string('d', 64), "src/net/File.cs")] };
        Reject(() => PackageBuilder.ValidateManifest(colliding, reviewedChain, [("File.cs", "src/net/File.cs")]));

        const string secondPatch = "--- a/file.cs\n+++ b/file.cs\n@@ -1,2 +1,2 @@\n old\n-after\n+final\n";
        var intermediate = VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(patch), "file.cs");
        Check(Encoding.UTF8.GetString(VerifiedSources.ApplyPatch(intermediate, Encoding.UTF8.GetBytes(secondPatch), "file.cs")) == "old\nfinal\n");
        Reject(() => VerifiedSources.ApplyPatch(Encoding.UTF8.GetBytes("old\nbefore\n"), Encoding.UTF8.GetBytes(secondPatch), "file.cs"));
        Console.WriteLine($"Builder self-tests passed: {passed} (patch, CRLF, context, target, hunk counts, traversal, hash, symlink, package identity, reviewed change set, advisory references, chain continuity/order, added-file collisions and final source selection).");
        return 0;
    }
}
