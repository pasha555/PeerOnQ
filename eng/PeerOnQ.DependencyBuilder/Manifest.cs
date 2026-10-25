namespace PeerOnQ.DependencyBuilder;

internal sealed record DependencyManifest(
    int SchemaVersion, string RequiredSdkVersion, PackageIdentity Package, UpstreamIdentity Upstream, PatchIdentity[] Patches,
    AddedFileIdentity[]? AddedFiles = null, MitigatedAdvisory[]? MitigatedAdvisories = null)
{
    internal IEnumerable<PatchIdentity> FinalPatches => Patches
        .GroupBy(patch => patch.TargetPath, StringComparer.Ordinal).Select(group => group.Last());
    internal IReadOnlyList<AddedFileIdentity> Added => AddedFiles ?? [];
    internal string ProjectDirectory => Path.GetDirectoryName(Upstream.ProjectPath)!.Replace('\\', '/');
}
internal sealed record PackageIdentity(string Id, string Version, string AssemblyName, string AssemblyVersion);
internal sealed record UpstreamIdentity(string PackageId, string PackageVersion, string RepositoryUrl, string Commit,
    string ArchiveUrl, string ArchiveSha256, string ArchiveRoot, string ProjectPath, string LicensePath, string LicenseSha256);
internal sealed record PatchIdentity(string File, string Sha256, string TargetPath, string UpstreamFileSha256, string PatchedFileSha256);
/// <summary>A PeerOnQ-authored source file that does not exist upstream; shipped and embedded as written.</summary>
internal sealed record AddedFileIdentity(string File, string Sha256, string TargetPath);
/// <summary>An upstream advisory that affects the pinned upstream version and is fixed by the listed PeerOnQ changes.</summary>
internal sealed record MitigatedAdvisory(string Id, string Summary, string[] FixedBy);
