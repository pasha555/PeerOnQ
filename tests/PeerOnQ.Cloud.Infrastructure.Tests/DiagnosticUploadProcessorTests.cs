using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using PeerOnQ.Cloud.Api;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Infrastructure.Tests;

public sealed class DiagnosticUploadProcessorTests
{
    private const string UploadToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Disclosure =
        "Contains sanitized application logs and technical summaries only. No screen, clipboard, file content, credential, token, private key, password, full device ID, or personal document is included.";

    [Fact]
    public async Task Valid_client_compatible_archive_is_verified_before_persistence()
    {
        var bytes = CreateArchive(TextEntry(
            "logs/log-1.jsonl",
            "device=407-***-***-464 status=healthy token=[redacted]\n"));
        var diagnosticId = Guid.NewGuid();
        var blobStore = new RecordingBlobStore();
        var diagnostics = new RecordingDiagnosticsService();
        var processor = new DiagnosticUploadProcessor(blobStore, diagnostics);

        var result = await ProcessAsync(processor, diagnosticId, bytes);

        Assert.Equal(DiagnosticStatusV1.Uploaded, result.Status);
        Assert.Equal(1, blobStore.StoreCalls);
        Assert.Equal(bytes.Length, blobStore.StoredBytes);
        Assert.Equal(1, diagnostics.CompleteCalls);
        Assert.NotNull(diagnostics.CompletedRequest);
        Assert.Equal(bytes.Length, diagnostics.CompletedRequest.SanitizedArchiveSizeBytes);
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(bytes)), diagnostics.CompletedRequest.Sha256Base64);
        AssertNoIngressFile(diagnosticId);
    }

    [Theory]
    [InlineData("screen.png", "not-really-an-image")]
    [InlineData("files/document.txt", "personal document content")]
    public async Task Image_and_file_entries_are_rejected_without_persistence(string name, string content)
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(TextEntry(name, content)));
    }

    [Theory]
    [InlineData("token=raw-upload-token-value")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nraw-private-key-material\n-----END PRIVATE KEY-----")] // secret-scan: allow-test-vector
    public async Task Token_and_private_key_content_are_rejected_without_persistence(string content)
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(TextEntry("logs/log-1.jsonl", content)));
    }

    [Fact]
    public async Task Path_traversal_entry_is_rejected_without_persistence()
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(TextEntry("logs/../secret.txt", "hidden")));
    }

    [Fact]
    public async Task Highly_compressible_entry_is_rejected_without_persistence()
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(TextEntry(
            "logs/log-1.jsonl",
            new string('A', 1024 * 1024))));
    }

    [Fact]
    public async Task Duplicate_entry_is_rejected_without_persistence()
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(
            TextEntry("logs/log-1.jsonl", "first"),
            TextEntry("logs/log-1.jsonl", "second")));
    }

    [Fact]
    public async Task Encrypted_flag_is_rejected_without_persistence()
    {
        var bytes = CreateArchive(TextEntry("logs/log-1.jsonl", "healthy"));
        SetFirstEntryEncryptionFlag(bytes);

        await AssertRejectedWithoutPersistenceAsync(bytes);
    }

    [Fact]
    public async Task Symlink_entry_is_rejected_without_persistence()
    {
        var bytes = CreateArchive(TextEntry("logs/log-1.jsonl", "healthy"));
        MarkEntryAsUnixSymlink(bytes, "logs/log-1.jsonl");

        await AssertRejectedWithoutPersistenceAsync(bytes);
    }

    [Fact]
    public async Task Binary_log_is_rejected_without_persistence()
    {
        await AssertRejectedWithoutPersistenceAsync(CreateArchive(
            new ArchiveEntry("logs/log-1.jsonl", [0xff, 0xfe, 0x00, 0x01])));
    }

    [Fact]
    public async Task Corrupt_archive_is_rejected_without_persistence()
    {
        var valid = CreateArchive(TextEntry("logs/log-1.jsonl", "healthy"));
        var corrupt = valid[..^11];

        await AssertRejectedWithoutPersistenceAsync(corrupt);
    }

    private static async Task AssertRejectedWithoutPersistenceAsync(byte[] bytes)
    {
        var diagnosticId = Guid.NewGuid();
        var blobStore = new RecordingBlobStore();
        var diagnostics = new RecordingDiagnosticsService();
        var processor = new DiagnosticUploadProcessor(blobStore, diagnostics);

        var exception = await Assert.ThrowsAsync<CloudServiceException>(
            () => ProcessAsync(processor, diagnosticId, bytes));

        Assert.Equal(CloudErrorCodes.DiagnosticUploadRejected, exception.Code);
        Assert.Equal(0, blobStore.StoreCalls);
        Assert.Equal(0, blobStore.StoredBytes);
        Assert.Equal(0, diagnostics.CompleteCalls);
        AssertNoIngressFile(diagnosticId);
    }

    private static async Task<DiagnosticStatusResponseV1> ProcessAsync(
        DiagnosticUploadProcessor processor,
        Guid diagnosticId,
        byte[] bytes)
    {
        await using var source = new MemoryStream(bytes, writable: false);
        var formFile = new FormFile(source, 0, bytes.Length, "archive", "peeronq-diagnostics.zip")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/zip",
        };
        return await processor.ProcessAsync(
            new DeviceAccessPrincipal(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10)),
            diagnosticId,
            UploadToken,
            Convert.ToBase64String(SHA256.HashData(bytes)),
            formFile,
            CancellationToken.None);
    }

    private static byte[] CreateArchive(params ArchiveEntry[] logEntries)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, new ArchiveEntry("manifest.json", CreateManifest()));
            foreach (var entry in logEntries) WriteEntry(archive, entry);
        }
        return output.ToArray();
    }

    private static byte[] CreateManifest()
    {
        var createdAtUtc = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            diagnosticId = Guid.NewGuid(),
            createdAtUtc,
            expiresAtUtc = createdAtUtc.AddDays(14),
            appVersion = "0.6.0",
            operatingSystem = "Microsoft Windows 11",
            architecture = "X64",
            databaseSchemaVersion = 3,
            updateStatus = "current",
            connectionFailureCodes = new[] { "turn_unavailable" },
            webRtcStatistics = new Dictionary<string, double> { ["rttMs"] = 42 },
            healthChecks = new Dictionary<string, string> { ["database"] = "healthy" },
            disclosure = Disclosure,
        });
    }

    private static ArchiveEntry TextEntry(string name, string content) =>
        new(name, new UTF8Encoding(false).GetBytes(content));

    private static void WriteEntry(ZipArchive archive, ArchiveEntry source)
    {
        var entry = archive.CreateEntry(source.Name, CompressionLevel.SmallestSize);
        using var destination = entry.Open();
        destination.Write(source.Content);
    }

    private static void SetFirstEntryEncryptionFlag(byte[] archive)
    {
        Assert.Equal(0x04034b50u, ReadUInt32(archive, 0));
        WriteUInt16(archive, 6, (ushort)(ReadUInt16(archive, 6) | 0x0001));
        var centralOffset = GetCentralDirectoryOffset(archive);
        Assert.Equal(0x02014b50u, ReadUInt32(archive, centralOffset));
        WriteUInt16(archive, centralOffset + 8, (ushort)(ReadUInt16(archive, centralOffset + 8) | 0x0001));
    }

    private static void MarkEntryAsUnixSymlink(byte[] archive, string name)
    {
        var offset = GetCentralDirectoryOffset(archive);
        while (ReadUInt32(archive, offset) == 0x02014b50)
        {
            var nameLength = ReadUInt16(archive, offset + 28);
            var extraLength = ReadUInt16(archive, offset + 30);
            var commentLength = ReadUInt16(archive, offset + 32);
            var entryName = Encoding.UTF8.GetString(archive, offset + 46, nameLength);
            if (string.Equals(entryName, name, StringComparison.Ordinal))
            {
                archive[offset + 5] = 3;
                BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(offset + 38, sizeof(uint)), 0xA1FF0000u);
                return;
            }
            offset += 46 + nameLength + extraLength + commentLength;
        }
        throw new InvalidOperationException("The requested ZIP entry was not found.");
    }

    private static int GetCentralDirectoryOffset(byte[] archive)
    {
        var footer = archive.Length - 22;
        Assert.Equal(0x06054b50u, ReadUInt32(archive, footer));
        return checked((int)ReadUInt32(archive, footer + 16));
    }

    private static ushort ReadUInt16(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(offset, sizeof(ushort)));

    private static uint ReadUInt32(byte[] source, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset, sizeof(uint)));

    private static void WriteUInt16(byte[] destination, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(destination.AsSpan(offset, sizeof(ushort)), value);

    private static void AssertNoIngressFile(Guid diagnosticId)
    {
        var ingressRoot = Path.Combine(Path.GetTempPath(), "peeronq-diagnostic-ingress");
        if (!Directory.Exists(ingressRoot)) return;
        Assert.Empty(Directory.EnumerateFiles(ingressRoot, $"{diagnosticId:N}-*.partial", SearchOption.TopDirectoryOnly));
    }

    private sealed class RecordingBlobStore : IDiagnosticBlobStore
    {
        public int StoreCalls { get; private set; }
        public long StoredBytes { get; private set; }

        public async Task StoreAsync(
            Guid diagnosticId,
            string verifiedArchivePath,
            long sizeBytes,
            CancellationToken cancellationToken)
        {
            StoreCalls++;
            var bytes = await File.ReadAllBytesAsync(verifiedArchivePath, cancellationToken);
            Assert.Equal(sizeBytes, bytes.LongLength);
            StoredBytes += bytes.LongLength;
        }

        public Task DeleteAsync(Guid diagnosticId, string? objectKey, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class RecordingDiagnosticsService : IDiagnosticsService
    {
        public int CompleteCalls { get; private set; }
        public DiagnosticUploadCompleteRequestV1? CompletedRequest { get; private set; }

        public Task<DiagnosticCreateResultV1> CreateRequestAsync(
            DeviceAccessPrincipal caller,
            DiagnosticCreateRequestV1 request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DiagnosticStatusResponseV1> CompleteUploadAsync(
            DeviceAccessPrincipal caller,
            DiagnosticUploadCompleteRequestV1 request,
            CancellationToken cancellationToken = default)
        {
            CompleteCalls++;
            CompletedRequest = request;
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new DiagnosticStatusResponseV1(
                request.DiagnosticId,
                DiagnosticStatusV1.Uploaded,
                ConsentGranted: true,
                now,
                now.AddDays(14),
                "D-TEST"));
        }

        public Task<DiagnosticStatusResponseV1> GetStatusAsync(
            DeviceAccessPrincipal caller,
            Guid diagnosticId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed record ArchiveEntry(string Name, byte[] Content);
}
