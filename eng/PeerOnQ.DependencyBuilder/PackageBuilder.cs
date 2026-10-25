using System.Diagnostics;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PeerOnQ.DependencyBuilder;

internal static class PackageBuilder
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>
    /// The reviewed PeerOnQ changes to the pinned upstream source, in application order. A manifest
    /// that names any other patch, target or order is rejected, so an unreviewed change cannot be built.
    /// </summary>
    internal static readonly (string File, string TargetPath)[] ReviewedPatches = [
        ("dtls-receive-oversize-and-close-wakeup.patch", "src/net/DtlsSrtp/DtlsSrtpTransport.cs"),
        ("srtcp-replay-window.patch", "src/net/DtlsSrtp/Transform/SrtcpCryptoContext.cs"),
        ("srtcp-context-after-authentication.patch", "src/net/DtlsSrtp/Transform/SrtcpTransformer.cs"),
        ("srtp-authenticated-state-update.patch", "src/net/DtlsSrtp/Transform/SrtpCryptoContext.cs"),
        ("srtp-context-after-authentication.patch", "src/net/DtlsSrtp/Transform/SrtpTransformer.cs"),
        ("rtpicechannel-turn-tls.patch", "src/net/ICE/RtpIceChannel.cs"),
        ("rtcp-compound-length-bounds.patch", "src/net/RTCP/RTCPCompoundPacket.cs"),
        ("rtcp-feedback-length-bounds.patch", "src/net/RTCP/RTCPFeedback.cs"),
        ("rtcp-sdes-cname-bounds.patch", "src/net/RTCP/RTCPSdesReport.cs"),
        ("rtcp-twcc-truncated-deltas.patch", "src/net/RTCP/RTCPTWCCFeedback.cs"),
        ("rtcp-first-transit-initialization.patch", "src/net/RTCP/ReceptionReport.cs"),
        ("rtp-channel-receive-hardening.patch", "src/net/RTP/RTPChannel.cs"),
        ("rtp-header-bounds.patch", "src/net/RTP/RTPHeader.cs"),
        ("sctp-chunk-length-bounds.patch", "src/net/SCTP/Chunks/SctpChunk.cs"),
        ("sctp-sack-report-count-bounds.patch", "src/net/SCTP/Chunks/SctpSackChunk.cs"),
        ("sctp-data-receiver-sack-credit.patch", "src/net/SCTP/SctpDataReceiver.cs"),
        ("sctp-data-sender-flow-control.patch", "src/net/SCTP/SctpDataSender.cs"),
        ("sctp-packet-chunk-bounds.patch", "src/net/SCTP/SctpPacket.cs"),
        ("stun-attribute-length-checks.patch", "src/net/STUN/STUNAttributes/STUNAttribute.cs"),
        ("stun-header-empty-segment.patch", "src/net/STUN/STUNHeader.cs"),
        ("stun-message-length-checks.patch", "src/net/STUN/STUNMessage.cs"),
        ("dtls-handshake-dedicated-thread.patch", "src/net/WebRTC/RTCPeerConnection.cs"),
        ("rtcsctp-receive-loop-resilience.patch", "src/net/WebRTC/RTCSctpTransport.cs")
    ];

    /// <summary>The reviewed PeerOnQ-authored files added to the upstream tree.</summary>
    internal static readonly (string File, string TargetPath)[] ReviewedAddedFiles = [
        ("TurnTlsConnection.cs", "src/net/ICE/TurnTlsConnection.cs")
    ];

    internal static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.SequenceEqual(["--self-test"], StringComparer.Ordinal))
            return await BuilderSelfTests.RunAsync(token);
        var root = FindRepository();
        var manifestDirectory = Path.Combine(root, "eng", "sipsorcery");
        var manifestPath = Path.Combine(manifestDirectory, "upstream.json");
        var manifest = JsonSerializer.Deserialize<DependencyManifest>(await File.ReadAllTextAsync(manifestPath, token), Json)
                       ?? throw new InvalidDataException("Missing dependency manifest.");
        var output = Path.Combine(manifestDirectory, "packages");
        string? archiveCache = null;
        var dotnet = ResolveDotnet(root);
        for (var index = 0; index < args.Length; index++)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Options require a value.");
            switch (args[index])
            {
                case "--output": output = Path.GetFullPath(args[++index]); break;
                case "--archive-cache": archiveCache = Path.GetFullPath(args[++index]); break;
                case "--dotnet": dotnet = Path.GetFullPath(args[++index]); break;
                default: throw new ArgumentException("Unknown dependency builder option.");
            }
        }
        ValidateManifest(manifest);
        var central = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
        if (central.Descendants("PeerOnQSipsorceryPackageVersion").Single().Value != manifest.Package.Version)
            throw new InvalidDataException("Central derivative package version and source manifest differ.");
        var sdk = (await RunProcessAsync(dotnet, ["--version"], root, null, token)).Trim();
        if (sdk != manifest.RequiredSdkVersion) throw new InvalidOperationException($"SDK {manifest.RequiredSdkVersion} is required; selected {sdk}.");
        foreach (var patch in manifest.Patches)
            VerifiedSources.RequireHash(Path.Combine(manifestDirectory, patch.File), patch.Sha256);
        foreach (var added in manifest.Added)
            VerifiedSources.RequireHash(Path.Combine(manifestDirectory, "added", added.File), added.Sha256);
        Directory.CreateDirectory(output);
        var packageName = manifest.Package.Id + "." + manifest.Package.Version + ".nupkg";
        var packagePath = Path.Combine(output, packageName);
        var provenancePath = packagePath + ".provenance.json";
        if (File.Exists(packagePath))
        {
            if (!File.Exists(provenancePath)) throw new InvalidDataException("Cached package lacks build provenance.");
            using var provenance = JsonDocument.Parse(await File.ReadAllTextAsync(provenancePath, token));
            VerifiedSources.RequireHash(packagePath, provenance.RootElement.GetProperty("packageSha256").GetString()!);
            var cachedAssemblyHash = ValidatePackage(packagePath, manifest, manifestPath);
            if (cachedAssemblyHash != provenance.RootElement.GetProperty("assemblySha256").GetString()
                || provenance.RootElement.GetProperty("sdk").GetString() != sdk
                || provenance.RootElement.GetProperty("manifestSha256").GetString() != VerifiedSources.Sha(manifestPath))
                throw new InvalidDataException("Cached package build provenance differs from current manifest.");
            Console.WriteLine($"Verified cached {packageName}: {VerifiedSources.Sha(packagePath)}");
            return 0;
        }

        var archive = archiveCache ?? Path.Combine(output, ".cache", "sipsorcery-" + manifest.Upstream.Commit + ".tar.gz");
        if (!File.Exists(archive))
        {
            if (archiveCache is not null) throw new FileNotFoundException("Explicit source archive cache does not exist.", archive);
            Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
            await DownloadAsync(manifest.Upstream.ArchiveUrl, archive, manifest.Upstream.ArchiveSha256, token);
        }
        VerifiedSources.RequireHash(archive, manifest.Upstream.ArchiveSha256);
        var work = Path.Combine(Path.GetTempPath(), "peeronq-sipsorcery-build-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        Console.WriteLine("Dependency build evidence: " + work);
        var source = Path.Combine(work, "source");
        Directory.CreateDirectory(source);
        await VerifiedSources.ExtractAsync(archive, source, manifest.Upstream.ArchiveRoot, token);
        var changedSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var patch in manifest.Patches)
        {
            var target = VerifiedSources.ContainedPath(source, patch.TargetPath);
            VerifiedSources.RequireHash(target, patch.UpstreamFileSha256);
            var patched = VerifiedSources.ApplyPatch(await File.ReadAllBytesAsync(target, token),
                await File.ReadAllBytesAsync(Path.Combine(manifestDirectory, patch.File), token), patch.TargetPath);
            if (VerifiedSources.Sha(patched) != patch.PatchedFileSha256)
                throw new InvalidDataException("Patched source hash does not match reviewed output.");
            await File.WriteAllBytesAsync(target, patched, token);
            changedSources.Add(target);
        }
        foreach (var added in manifest.Added)
        {
            var target = VerifiedSources.ContainedPath(source, added.TargetPath);
            if (File.Exists(target)) throw new InvalidDataException("An added file would replace an upstream file.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(manifestDirectory, "added", added.File), target, overwrite: false);
            VerifiedSources.RequireHash(target, added.Sha256);
            changedSources.Add(target);
        }
        var license = VerifiedSources.ContainedPath(source, manifest.Upstream.LicensePath);
        VerifiedSources.RequireHash(license, manifest.Upstream.LicenseSha256);
        await File.WriteAllTextAsync(Path.Combine(source, "global.json"), JsonSerializer.Serialize(new { sdk = new { version = manifest.RequiredSdkVersion, rollForward = "disable" } }, Json), token);
        await File.WriteAllTextAsync(Path.Combine(source, "NuGet.Config"), "<configuration><packageSources><clear/><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\"/></packageSources><packageSourceMapping><clear/><packageSource key=\"nuget.org\"><package pattern=\"*\"/></packageSource></packageSourceMapping></configuration>", token);
        var sourceLink = Path.Combine(work, "source-link.json");
        var documents = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(file => !changedSources.Contains(file))
            .OrderBy(file => Path.GetRelativePath(source, file).Replace('\\', '/'), StringComparer.Ordinal)
            .ToDictionary(file => "/_/sipsorcery/" + Path.GetRelativePath(source, file).Replace('\\', '/'),
                file => "https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/" + manifest.Upstream.Commit + "/" + Path.GetRelativePath(source, file).Replace('\\', '/'));
        await File.WriteAllTextAsync(sourceLink, JsonSerializer.Serialize(new { documents }, Json), token);
        var sourceProvenance = Path.Combine(work, "source-provenance.json");
        var noticeTargets = Path.Combine(manifestDirectory, "PeerOnQ.SIPSorcery.targets");
        await File.WriteAllTextAsync(sourceProvenance, JsonSerializer.Serialize(new
        {
            packageId = manifest.Package.Id, packageVersion = manifest.Package.Version,
            upstreamPackageId = manifest.Upstream.PackageId, upstreamPackageVersion = manifest.Upstream.PackageVersion,
            upstreamCommit = manifest.Upstream.Commit, archiveSha256 = manifest.Upstream.ArchiveSha256,
            licenseSha256 = manifest.Upstream.LicenseSha256,
            patches = manifest.Patches, addedFiles = manifest.Added, mitigatedAdvisories = manifest.MitigatedAdvisories ?? [],
            manifestSha256 = VerifiedSources.Sha(manifestPath), sdk,
            noticeTargetsSha256 = VerifiedSources.Sha(noticeTargets),
            advisoryStatus = "Upstream advisories that affect the pinned version are listed with the PeerOnQ changes that fix them; run the independent upstream advisory gate.",
            sourceLink = "Pinned unchanged upstream documents; exact changed and added source embedded in PDB and package."
        }, Json), token);
        ConfigureProjects(source, manifest, sourceLink, sourceProvenance, manifestPath);
        var project = VerifiedSources.ContainedPath(source, manifest.Upstream.ProjectPath);
        var common = new[] { "-p:TargetFrameworks=net10.0", "-p:RestoreEnablePackagePruning=false", "-p:GeneratePackageOnBuild=false", "-p:EnableSourceControlManagerQueries=false", "-p:EnableSourceLink=false", "-p:EmbedAllSources=true", "-p:Deterministic=true" };
        await RunProcessAsync(dotnet, ["build", project, "-c", "Release", "--nologo", "-m:1", .. common], source, Path.Combine(work, "build.log"), token);
        var packed = Path.Combine(work, "packages");
        await RunProcessAsync(dotnet, ["pack", project, "-c", "Release", "--no-build", "--nologo", "--output", packed, .. common], source, Path.Combine(work, "pack.log"), token);
        var built = Path.Combine(packed, packageName);
        DeterministicPackage.Normalize(built);
        ValidatePackage(built, manifest, manifestPath);
        var hash = VerifiedSources.Sha(built);
        var dll = Path.Combine(source, manifest.ProjectDirectory, "bin", "Release", "net10.0", "SIPSorcery.dll");
        await File.WriteAllTextAsync(provenancePath, JsonSerializer.Serialize(new
        {
            packageId = manifest.Package.Id, packageVersion = manifest.Package.Version,
            packageSha256 = hash, assemblySha256 = VerifiedSources.Sha(dll),
            upstreamPackageId = manifest.Upstream.PackageId, upstreamPackageVersion = manifest.Upstream.PackageVersion,
            upstreamCommit = manifest.Upstream.Commit, archiveSha256 = manifest.Upstream.ArchiveSha256,
            manifestSha256 = VerifiedSources.Sha(manifestPath),
            patches = manifest.Patches, addedFiles = manifest.Added,
            sdk, evidenceDirectory = work, builtUtc = DateTimeOffset.UtcNow,
            advisoryCleanlinessClaim = false
        }, Json), token);
        File.Copy(built, packagePath, overwrite: false);
        await File.WriteAllTextAsync(packagePath + ".sha256", hash + "  " + packageName + "\n", token);
        Console.WriteLine($"Built {packageName}: {hash}");
        return 0;
    }

    private static void ConfigureProjects(string source, DependencyManifest manifest, string sourceLink, string provenance, string manifestPath)
    {
        var projectPath = VerifiedSources.ContainedPath(source, manifest.Upstream.ProjectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        var project = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
        if (project.Descendants("Version").Single().Value != manifest.Upstream.PackageVersion)
            throw new InvalidDataException("Upstream source project version differs from manifest.");
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        project.Root!.Add(new XElement("PropertyGroup",
            new XElement("PackageId", manifest.Package.Id), new XElement("PackageVersion", manifest.Package.Version),
            new XElement("InformationalVersion", manifest.Package.Version + "+peeronq.upstream." + manifest.Upstream.Commit),
            new XElement("Description", "PeerOnQ maintained SIPSorcery derivative; upstream " + manifest.Upstream.PackageVersion + " at " + manifest.Upstream.Commit + " (BSD-3-Clause); PeerOnQ reliability and security changes are listed in _PeerOnQ/upstream.json."),
            // Ship the upstream license text (with its DTLS/SRTP notice) as the package license.
            new XElement("PackageLicenseExpression", ""), new XElement("PackageLicenseFile", manifest.Upstream.LicensePath),
            new XElement("SourceLink", sourceLink), new XElement("PathMap", source + "=/_/sipsorcery"),
            new XElement("IncludeSymbols", "false")));
        var evidence = new XElement("ItemGroup",
            Packed(VerifiedSources.ContainedPath(source, manifest.Upstream.LicensePath), manifest.Upstream.LicensePath),
            Packed(provenance, "_PeerOnQ/provenance.json"), Packed(manifestPath, "_PeerOnQ/upstream.json"),
            Packed(Path.Combine(manifestDirectory, "PeerOnQ.SIPSorcery.targets"), "buildTransitive/PeerOnQ.SIPSorcery.targets"),
            Packed(Path.Combine(projectDirectory, "bin", "Release", "net10.0", "SIPSorcery.pdb"), "lib/net10.0/SIPSorcery.pdb"));
        foreach (var patch in manifest.Patches)
            evidence.Add(Packed(Path.Combine(manifestDirectory, patch.File), "_PeerOnQ/" + patch.File));
        foreach (var patch in manifest.FinalPatches)
        {
            var patchedSource = VerifiedSources.ContainedPath(source, patch.TargetPath);
            evidence.Add(new XElement("EmbeddedFiles", new XAttribute("Include", patchedSource)),
                Packed(patchedSource, "_PeerOnQ/patched-source/" + Path.GetFileName(patch.TargetPath)));
        }
        foreach (var added in manifest.Added)
        {
            var addedSource = VerifiedSources.ContainedPath(source, added.TargetPath);
            evidence.Add(new XElement("EmbeddedFiles", new XAttribute("Include", addedSource)),
                Packed(addedSource, "_PeerOnQ/added/" + added.File));
        }
        project.Root.Add(evidence);
        project.Save(projectPath, SaveOptions.DisableFormatting);
    }

    private static XElement Packed(string file, string packagePath) => new("None", new XAttribute("Include", file), new XAttribute("Pack", "true"), new XAttribute("PackagePath", packagePath));

    internal static string ValidatePackage(string package, DependencyManifest manifest, string manifestPath)
    {
        ValidateManifest(manifest);
        using var archive = ZipFile.OpenRead(package);
        foreach (var item in archive.Entries) _ = VerifiedSources.ContainedPath(Path.GetTempPath(), item.FullName.TrimEnd('/'));
        var nuspecs = archive.Entries.Where(item => item.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).ToArray();
        if (nuspecs.Length != 1) throw new InvalidDataException("Package must contain one nuspec.");
        using var nuspecStream = nuspecs[0].Open();
        var nuspec = XDocument.Load(nuspecStream);
        var metadata = nuspec.Descendants().Single(element => element.Name.LocalName == "metadata");
        string Value(string name) => metadata.Elements().Single(element => element.Name.LocalName == name).Value;
        if (Value("id") != manifest.Package.Id || Value("version") != manifest.Package.Version || Value("license") != manifest.Upstream.LicensePath)
            throw new InvalidDataException("Package identity or license metadata mismatch.");
        if (metadata.Descendants().Any(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") is "SIPSorcery" or "PeerOnQ.SIPSorcery"))
            throw new InvalidDataException("Package introduces a conflicting SIPSorcery dependency.");
        if (!metadata.Descendants().Any(element => element.Name.LocalName == "dependency" && (string?)element.Attribute("id") == "SIPSorceryMedia.Abstractions"))
            throw new InvalidDataException("Original media abstraction dependency was lost.");
        var assemblies = archive.Entries.Where(item => item.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (assemblies.Length != 1 || assemblies[0].FullName != "lib/net10.0/SIPSorcery.dll")
            throw new InvalidDataException("Package must ship exactly the original net10 SIPSorcery assembly.");
        using var assemblyStream = assemblies[0].Open();
        using var assemblyBytes = new MemoryStream();
        assemblyStream.CopyTo(assemblyBytes); assemblyBytes.Position = 0;
        using var pe = new PEReader(assemblyBytes);
        var reader = pe.GetMetadataReader();
        var definition = reader.GetAssemblyDefinition();
        if (reader.GetString(definition.Name) != manifest.Package.AssemblyName || definition.Version.ToString() != manifest.Package.AssemblyVersion)
            throw new InvalidDataException("Original assembly identity changed.");
        byte[] Read(string name)
        {
            using var stream = archive.GetEntry(name)?.Open() ?? throw new InvalidDataException("Required package evidence is missing: " + name);
            using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray();
        }
        var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
        if (VerifiedSources.Sha(Read(manifest.Upstream.LicensePath)) != manifest.Upstream.LicenseSha256
            || VerifiedSources.Sha(Read("buildTransitive/PeerOnQ.SIPSorcery.targets")) != VerifiedSources.Sha(Path.Combine(manifestDirectory, "PeerOnQ.SIPSorcery.targets")))
            throw new InvalidDataException("Package source/license evidence changed.");
        foreach (var patch in manifest.Patches)
            if (VerifiedSources.Sha(Read("_PeerOnQ/" + patch.File)) != patch.Sha256)
                throw new InvalidDataException("Package patch evidence changed.");
        foreach (var patch in manifest.FinalPatches)
            if (VerifiedSources.Sha(Read("_PeerOnQ/patched-source/" + Path.GetFileName(patch.TargetPath))) != patch.PatchedFileSha256)
                throw new InvalidDataException("Package final source evidence changed.");
        foreach (var addedFile in manifest.Added)
            if (VerifiedSources.Sha(Read("_PeerOnQ/added/" + addedFile.File)) != addedFile.Sha256)
                throw new InvalidDataException("Package added source evidence changed.");
        VerifyPdb(Read("lib/net10.0/SIPSorcery.pdb"), manifest);
        using var provenance = JsonDocument.Parse(Read("_PeerOnQ/provenance.json"));
        if (provenance.RootElement.GetProperty("archiveSha256").GetString() != manifest.Upstream.ArchiveSha256
            || provenance.RootElement.GetProperty("sdk").GetString() != manifest.RequiredSdkVersion
            || provenance.RootElement.GetProperty("licenseSha256").GetString() != manifest.Upstream.LicenseSha256
            || provenance.RootElement.GetProperty("noticeTargetsSha256").GetString() != VerifiedSources.Sha(Path.Combine(manifestDirectory, "PeerOnQ.SIPSorcery.targets"))
            || provenance.RootElement.GetProperty("manifestSha256").GetString() != VerifiedSources.Sha(manifestPath)
            || VerifiedSources.Sha(Read("_PeerOnQ/upstream.json")) != VerifiedSources.Sha(manifestPath))
            throw new InvalidDataException("Cached package belongs to different source inputs.");
        var patches = provenance.RootElement.GetProperty("patches").Deserialize<PatchIdentity[]>(Json);
        var added = provenance.RootElement.GetProperty("addedFiles").Deserialize<AddedFileIdentity[]>(Json);
        if (!(patches ?? []).SequenceEqual(manifest.Patches) || !(added ?? []).SequenceEqual(manifest.Added))
            throw new InvalidDataException("Cached package changes differ from manifest.");
        return VerifiedSources.Sha(assemblyBytes.ToArray());
    }

    private static void VerifyPdb(byte[] bytes, DependencyManifest manifest)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(bytes));
        var reader = provider.GetMetadataReader();
        var embeddedSourceKind = new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
        var sourceLinkKind = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
        var expectedDocuments = manifest.FinalPatches.Select(patch => (patch.TargetPath, Hash: patch.PatchedFileSha256))
            .Concat(manifest.Added.Select(added => (added.TargetPath, Hash: added.Sha256)))
            .ToDictionary(item => "/_/sipsorcery/" + item.TargetPath, item => item.Hash, StringComparer.Ordinal);
        var embeddedVerified = new HashSet<string>(StringComparer.Ordinal);
        var sourceLinkVerified = false;
        foreach (var handle in reader.CustomDebugInformation)
        {
            var info = reader.GetCustomDebugInformation(handle);
            var kind = reader.GetGuid(info.Kind);
            if (kind == sourceLinkKind)
            {
                using var sourceLink = JsonDocument.Parse(reader.GetBlobBytes(info.Value));
                var documents = sourceLink.RootElement.GetProperty("documents");
                if (!documents.EnumerateObject().Any()
                    || expectedDocuments.Keys.Any(name => documents.TryGetProperty(name, out _))
                    || documents.EnumerateObject().Any(item => !item.Value.GetString()!.StartsWith(
                        "https://raw.githubusercontent.com/sipsorcery-org/sipsorcery/" + manifest.Upstream.Commit + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("PDB SourceLink does not identify pinned unchanged upstream sources.");
                sourceLinkVerified = true;
            }
            if (kind != embeddedSourceKind || info.Parent.Kind != HandleKind.Document) continue;
            var document = reader.GetDocument((DocumentHandle)info.Parent);
            var documentName = reader.GetString(document.Name);
            if (!expectedDocuments.TryGetValue(documentName, out var expectedHash)) continue;
            var blob = reader.GetBlobBytes(info.Value);
            if (blob.Length < 4) throw new InvalidDataException("PDB embedded source is truncated.");
            var uncompressed = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(blob);
            if (uncompressed is < 0 or > 1024 * 1024) throw new InvalidDataException("PDB embedded source exceeds bounds.");
            byte[] source;
            if (uncompressed == 0) source = blob[4..];
            else
            {
                using var compressed = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
                source = new byte[uncompressed];
                compressed.ReadExactly(source);
                if (compressed.ReadByte() != -1) throw new InvalidDataException("PDB embedded source length mismatch.");
            }
            if (VerifiedSources.Sha(source) != expectedHash)
                throw new InvalidDataException("PDB does not embed the exact reviewed changed source.");
            embeddedVerified.Add(documentName);
        }
        if (embeddedVerified.Count != expectedDocuments.Count || !sourceLinkVerified)
            throw new InvalidDataException("PDB must retain pinned SourceLink and exact embedded changed source.");
    }

    internal static void ValidateManifest(DependencyManifest manifest) =>
        ValidateManifest(manifest, ReviewedPatches, ReviewedAddedFiles);

    internal static void ValidateManifest(DependencyManifest manifest,
        IReadOnlyList<(string File, string TargetPath)> reviewedPatches, IReadOnlyList<(string File, string TargetPath)> reviewedAdded)
    {
        var patches = manifest.Patches ?? [];
        var added = manifest.Added;
        if (manifest.SchemaVersion != 2 || manifest.RequiredSdkVersion != "10.0.402"
            || manifest.Package.Id != "PeerOnQ.SIPSorcery" || manifest.Package.AssemblyName != "SIPSorcery"
            || manifest.Upstream.PackageId != "SIPSorcery" || !Regex.IsMatch(manifest.Upstream.Commit, "^[a-f0-9]{40}$", RegexOptions.CultureInvariant)
            || manifest.Upstream.ArchiveRoot != "sipsorcery-" + manifest.Upstream.Commit
            || manifest.Upstream.ArchiveUrl != "https://codeload.github.com/sipsorcery-org/sipsorcery/tar.gz/" + manifest.Upstream.Commit
            || manifest.Upstream.ProjectPath != "src/SIPSorcery.csproj" || manifest.Upstream.LicensePath != "LICENSE.md"
            || patches.Length == 0
            || !patches.Select(patch => (patch.File, patch.TargetPath)).SequenceEqual(reviewedPatches)
            || !added.Select(file => (file.File, file.TargetPath)).SequenceEqual(reviewedAdded)
            || patches.Any(patch => !Regex.IsMatch(patch.File, "^[a-z0-9]+(-[a-z0-9]+)*\\.patch$", RegexOptions.CultureInvariant)
                                    || !patch.TargetPath.StartsWith("src/", StringComparison.Ordinal)
                                    || !patch.TargetPath.EndsWith(".cs", StringComparison.Ordinal))
            || patches.Select(patch => patch.File).Distinct(StringComparer.Ordinal).Count() != patches.Length
            || added.Any(file => !Regex.IsMatch(file.File, "^[A-Za-z0-9]+\\.cs$", RegexOptions.CultureInvariant)
                                 || !file.TargetPath.StartsWith("src/net/", StringComparison.Ordinal)
                                 || !file.TargetPath.EndsWith("/" + file.File, StringComparison.Ordinal))
            || added.Any(file => patches.Any(patch => patch.TargetPath == file.TargetPath)))
            throw new InvalidDataException("Unsupported dependency manifest.");
        foreach (var chain in patches.GroupBy(patch => patch.TargetPath, StringComparer.Ordinal))
        {
            var previous = chain.First();
            foreach (var patch in chain.Skip(1))
            {
                if (previous.PatchedFileSha256 != patch.UpstreamFileSha256)
                    throw new InvalidDataException("Patch chain does not continue from the reviewed previous output.");
                previous = patch;
            }
        }
        var changeNames = patches.Select(patch => patch.File).Concat(added.Select(file => file.File)).ToHashSet(StringComparer.Ordinal);
        foreach (var advisory in manifest.MitigatedAdvisories ?? [])
            if (!Regex.IsMatch(advisory.Id, "^GHSA(-[23456789cfghjmpqrvwx]{4}){3}$", RegexOptions.CultureInvariant)
                || string.IsNullOrWhiteSpace(advisory.Summary) || advisory.FixedBy.Length == 0
                || advisory.FixedBy.Any(name => !changeNames.Contains(name)))
                throw new InvalidDataException("Mitigated advisory must name reviewed PeerOnQ changes.");
    }

    private static async Task DownloadAsync(string url, string destination, string expectedHash, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        token = deadline.Token;
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PeerOnQ.DependencyBuilder/1");
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".download";
        await using (var stream = await response.Content.ReadAsStreamAsync(token))
        await using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[64 * 1024];
            int read;
            long received = 0;
            while ((read = await stream.ReadAsync(buffer, token)) != 0)
            {
                if ((received += read) > 256L * 1024 * 1024) throw new InvalidDataException("Source download exceeds bounds.");
                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        VerifiedSources.RequireHash(staging, expectedHash);
        File.Move(staging, destination, overwrite: false);
    }

    private static async Task<string> RunProcessAsync(string executable, string[] arguments, string workingDirectory, string? logFile, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the .NET SDK.");
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var text = await output + await error;
        if (logFile is not null) await File.WriteAllTextAsync(logFile, text, token);
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet command failed ({process.ExitCode}); evidence: {logFile ?? workingDirectory}\n{text}");
        return text;
    }

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) && Directory.Exists(Path.Combine(directory.FullName, "eng", "sipsorcery"))) return directory.FullName;
        throw new DirectoryNotFoundException("Dependency builder repository root not found.");
    }

    private static string ResolveDotnet(string repositoryRoot)
    {
        var file = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var localHost = Path.Combine(repositoryRoot, ".dotnet", file);
        if (File.Exists(localHost)) return localHost;
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(host) && File.Exists(host)) return host;
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        return !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, file)) ? Path.Combine(root, file) : file;
    }
}
