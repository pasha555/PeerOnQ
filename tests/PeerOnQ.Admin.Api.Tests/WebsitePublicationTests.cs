using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PeerOnQ.Observability;

namespace PeerOnQ.Admin.Api.Tests;

public sealed class WebsitePublicationTests
{
    [Fact]
    public void Verifier_accepts_the_expected_signed_website_manifest()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var signed = CreateSignedManifest(now);
        var verifier = new WebsitePublicationVerifier(Options.Create(signed.Options), new FixedTimeProvider(now));

        var verified = verifier.Verify(signed.Envelope);

        Assert.Equal("0.6.7", verified.Version);
        Assert.Equal("PeerOnQ-website-0.6.7.zip", verified.ArchiveFileName);
        Assert.Equal(new string('a', 64), verified.ArchiveSha256);
    }

    [Fact]
    public void Verifier_rejects_a_payload_changed_after_signing()
    {
        var now = new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero);
        var signed = CreateSignedManifest(now);
        using var document = JsonDocument.Parse(signed.Envelope);
        var root = document.RootElement;
        var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
        payload[^2] ^= 1;
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            keyId = root.GetProperty("keyId").GetString(),
            payload = Convert.ToBase64String(payload),
            signature = root.GetProperty("signature").GetString(),
        });
        var verifier = new WebsitePublicationVerifier(Options.Create(signed.Options), new FixedTimeProvider(now));

        var exception = Assert.Throws<ApiProblemException>(() => verifier.Verify(tampered));

        Assert.Equal("website_signature_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Extractor_rejects_parent_directory_traversal()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../escape.js").Open());
            writer.Write("unsafe");
        }
        var destination = Path.Combine(directory.Path, "release");
        Directory.CreateDirectory(destination);

        var exception = Assert.Throws<ApiProblemException>(() => WebsitePublicationService.ExtractVerifiedArchive(archivePath, destination));

        Assert.Equal("website_archive_path_invalid", exception.ErrorCode);
        Assert.False(File.Exists(Path.Combine(directory.Path, "escape.js")));
    }

    [Fact]
    public void Extractor_accepts_a_bounded_static_site()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "site.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            const string index = "<main>PeerOnQ</main>";
            Write(archive, "index.html", index);
            Write(archive, "peeronq-downloads-ui-v1.html", index);
            archive.CreateEntry("assets/").ExternalAttributes = 0x41ed << 16;
            Write(archive, "assets/app-123.js", "console.log('ok')");
            Write(archive, "assets/app-123.css", "body{margin:0}");
        }
        var destination = Path.Combine(directory.Path, "release");
        Directory.CreateDirectory(destination);

        WebsitePublicationService.ExtractVerifiedArchive(archivePath, destination);

        Assert.True(File.Exists(Path.Combine(destination, "index.html")));
        Assert.True(File.Exists(Path.Combine(destination, "assets", "app-123.js")));
    }

    [Fact]
    public void Extractor_rejects_a_website_without_the_verified_downloads_ui_entry()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "legacy-site.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "index.html", "<main>Legacy PeerOnQ</main>");
        }
        var destination = Path.Combine(directory.Path, "release");
        Directory.CreateDirectory(destination);

        var exception = Assert.Throws<ApiProblemException>(() => WebsitePublicationService.ExtractVerifiedArchive(archivePath, destination));

        Assert.Equal("website_downloads_ui_incompatible", exception.ErrorCode);
    }

    [Fact]
    public void Extractor_rejects_a_windows_installer_from_the_website_trust_boundary()
    {
        using var directory = new TemporaryDirectory();
        var archivePath = Path.Combine(directory.Path, "site-with-installer.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            Write(archive, "index.html", "<main>PeerOnQ</main>");
            Write(archive, "downloads/PeerOnQ-Windows-x64.msi", "not a website asset");
        }
        var destination = Path.Combine(directory.Path, "release");
        Directory.CreateDirectory(destination);

        var exception = Assert.Throws<ApiProblemException>(() => WebsitePublicationService.ExtractVerifiedArchive(archivePath, destination));

        Assert.Equal("website_archive_extension_invalid", exception.ErrorCode);
        Assert.False(File.Exists(Path.Combine(destination, "downloads", "PeerOnQ-Windows-x64.msi")));
    }

    private static (byte[] Envelope, WebsitePublicationOptions Options) CreateSignedManifest(DateTimeOffset now)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            productId = "com.peeronq.website",
            version = "0.6.7",
            issuedAt = now,
            expiresAt = now.AddDays(7),
            archiveFileName = "PeerOnQ-website-0.6.7.zip",
            archiveSha256 = new string('a', 64),
            archiveSizeBytes = 1024,
            entryPoint = "index.html",
        });
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            keyId = "website-test-1",
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
        });
        return (envelope, new WebsitePublicationOptions
        {
            Enabled = true,
            StorageDirectory = Path.GetTempPath(),
            KeyId = "website-test-1",
            PublicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            MaximumArchiveBytes = WebsitePublicationOptions.MaximumArchiveBytesLimit,
        });
    }

    private static void Write(ZipArchive archive, string name, string content)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"peeronq-website-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
