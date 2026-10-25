namespace PeerOnQ.DependencyBuilder;

internal sealed record DependencyManifest(
    int SchemaVersion, string RequiredSdkVersion, PackageIdentity Package, UpstreamIdentity Upstream, PatchIdentity Patch,
    PatchIdentity[]? AdditionalPatches = null)
{
    internal IEnumerable<PatchIdentity> Patches => new[] { Patch }.Concat(AdditionalPatches ?? []);
    internal IEnumerable<PatchIdentity> FinalPatches => Patches
        .GroupBy(patch => patch.TargetPath, StringComparer.Ordinal).Select(group => group.Last());
}
internal sealed record PackageIdentity(string Id, string Version, string AssemblyName, string AssemblyVersion);
internal sealed record UpstreamIdentity(string PackageId, string PackageVersion, string RepositoryUrl, string Commit,
    string ArchiveUrl, string ArchiveSha256, string ArchiveRoot, string LicensePath, string LicenseSha256);
internal sealed record PatchIdentity(string File, string Sha256, string TargetPath, string UpstreamFileSha256, string PatchedFileSha256);
