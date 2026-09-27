using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure.Diagnostics;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using PeerOnQ.Infrastructure.Updates;
using PeerOnQ.Shared.Contracts.V1;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class Phase5UpdateAndAuditTests
{
    [Fact]
    public void Signed_manifest_accepts_only_matching_newer_product_architecture_and_channel()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var envelope = Sign(CreateManifest(), key, options.KeyId);

        var update = new UpdateManifestVerifier().Verify(envelope, options);

        Assert.Equal(new Version(0, 6, 0, 0), update.Version);
        Assert.Equal("x64", update.Package.Architecture);
        Assert.Equal(UpdateCheckStatus.Available, new UpdateManifestVerifier().Classify(update, options));
    }

    [Theory]
    [InlineData("product", UpdateRejectionReason.WrongProduct)]
    [InlineData("architecture", UpdateRejectionReason.WrongArchitecture)]
    [InlineData("channel", UpdateRejectionReason.WrongChannel)]
    [InlineData("downgrade", UpdateRejectionReason.UnauthorizedDowngrade)]
    [InlineData("expired", UpdateRejectionReason.ExpiredManifest)]
    public void Signed_manifest_rejects_wrong_security_context(string mutation, UpdateRejectionReason expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var manifest = CreateManifest();
        manifest = mutation switch
        {
            "product" => manifest with { ProductId = "com.attacker.product" },
            "architecture" => manifest with { Packages = [manifest.Packages.Single() with { Architecture = "arm64" }] },
            "channel" => manifest with { Channel = UpdateChannel.Stable },
            "downgrade" => manifest with { Version = "0.4.0.0", MinimumSupportedVersion = "0.4.0.0" },
            "expired" => manifest with
            {
                IssuedAt = DateTimeOffset.UtcNow.AddDays(-2),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };

        var error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier().Verify(Sign(manifest, key, options.KeyId), options));
        Assert.Equal(expected, error.Reason);
    }

    [Fact]
    public void Signed_manifest_for_installed_version_reports_no_update()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var manifest = CreateManifest() with { Version = "0.5.1.0" };

        var update = new UpdateManifestVerifier().Verify(Sign(manifest, key, options.KeyId), options);

        Assert.Equal(UpdateCheckStatus.NoUpdate, new UpdateManifestVerifier().Classify(update, options));
    }

    [Fact]
    public void Legacy_product_id_is_accepted_during_the_peeronq_update_transition()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var manifest = CreateManifest() with { ProductId = UpdateClientOptions.LegacyProductId };

        var update = new UpdateManifestVerifier().Verify(Sign(manifest, key, options.KeyId), options);

        Assert.Equal(UpdateClientOptions.LegacyProductId, update.Manifest.ProductId);
    }

    [Fact]
    public void Tampered_signed_payload_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(Sign(CreateManifest(), key, options.KeyId), WebJson)!;
        var payload = Convert.FromBase64String(envelope.Payload);
        payload[^2] ^= 0x01;
        var tampered = JsonSerializer.SerializeToUtf8Bytes(envelope with { Payload = Convert.ToBase64String(payload) }, WebJson);

        var error = Assert.Throws<UpdateSecurityException>(() => new UpdateManifestVerifier().Verify(tampered, options));
        Assert.Equal(UpdateRejectionReason.InvalidSignature, error.Reason);
    }

    [Fact]
    public void Manifest_with_an_unknown_key_id_is_rejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(key);
        var envelope = JsonSerializer.Deserialize<SignedUpdateEnvelope>(Sign(CreateManifest(), key, "unknown-key"), WebJson)!;

        var error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier().Verify(JsonSerializer.SerializeToUtf8Bytes(envelope, WebJson), options));

        Assert.Equal(UpdateRejectionReason.UntrustedKey, error.Reason);
    }

    [Fact]
    public void Manifest_signed_by_an_untrusted_private_key_is_rejected()
    {
        using var trustedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attackerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var options = CreateOptions(trustedKey);

        var error = Assert.Throws<UpdateSecurityException>(() =>
            new UpdateManifestVerifier().Verify(Sign(CreateManifest(), attackerKey, options.KeyId), options));

        Assert.Equal(UpdateRejectionReason.InvalidSignature, error.Reason);
    }

    [Fact]
    public async Task Package_download_requires_exact_size_hash_authenticode_and_publisher()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var environment = new TempEnvironment();
        var packageBytes = Encoding.UTF8.GetBytes("signed-msi-test-payload");
        var fingerprint = new string('A', 64);
        var options = CreateOptions(key) with
        {
            UpdateDirectory = environment.Paths.UpdateDirectory,
            AllowedPublisherCertificateSha256 = new HashSet<string>(StringComparer.Ordinal) { fingerprint },
        };
        var descriptor = CreateManifest().Packages.Single() with
        {
            SizeBytes = packageBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
        };
        var verified = new VerifiedUpdate(
            CreateManifest() with { Packages = [descriptor] },
            descriptor,
            new Version(0, 6, 0, 0),
            new Version(0, 5, 0, 0));
        using var http = new HttpClient(new StaticContentHandler(packageBytes));
        var telemetry = new RecordingUpdateEventSink();
        var service = new UpdateService(
            http,
            options,
            new FixedAuthenticodeVerifier(true, fingerprint),
            telemetry: telemetry);

        var staged = await service.DownloadAsync(verified);

        Assert.True(File.Exists(staged.InstallerPath));
        Assert.Equal(packageBytes, await File.ReadAllBytesAsync(staged.InstallerPath));
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.UpdateDirectory, "*.partial"));
        Assert.Equal(UpdateEventKindV1.Downloaded, Assert.Single(telemetry.Events).Kind);
    }

    [Theory]
    [InlineData("hash", UpdateRejectionReason.HashMismatch)]
    [InlineData("signature", UpdateRejectionReason.InvalidAuthenticodeSignature)]
    [InlineData("publisher", UpdateRejectionReason.WrongPublisher)]
    [InlineData("incomplete", UpdateRejectionReason.HashMismatch)]
    [InlineData("oversized", UpdateRejectionReason.DownloadTooLarge)]
    public async Task Package_download_rejects_tampered_or_untrusted_installers(
        string failure,
        UpdateRejectionReason expected)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var environment = new TempEnvironment();
        var packageBytes = Encoding.UTF8.GetBytes("signed-msi-test-payload");
        var trustedFingerprint = new string('A', 64);
        var options = CreateOptions(key) with
        {
            UpdateDirectory = environment.Paths.UpdateDirectory,
            AllowedPublisherCertificateSha256 = new HashSet<string>(StringComparer.Ordinal) { trustedFingerprint },
        };
        var descriptor = CreateManifest().Packages.Single() with
        {
            SizeBytes = failure switch
            {
                "incomplete" => packageBytes.Length + 1,
                "oversized" => packageBytes.Length - 1,
                _ => packageBytes.Length,
            },
            Sha256 = failure == "hash"
                ? new string('0', 64)
                : Convert.ToHexString(SHA256.HashData(packageBytes)),
        };
        var verified = new VerifiedUpdate(
            CreateManifest() with { Packages = [descriptor] },
            descriptor,
            new Version(0, 6, 0, 0),
            new Version(0, 5, 0, 0));
        var authenticode = failure switch
        {
            "signature" => new FixedAuthenticodeVerifier(false, trustedFingerprint),
            "publisher" => new FixedAuthenticodeVerifier(true, new string('B', 64)),
            _ => new FixedAuthenticodeVerifier(true, trustedFingerprint),
        };
        using var http = new HttpClient(new StaticContentHandler(packageBytes));
        var telemetry = new RecordingUpdateEventSink();
        var service = new UpdateService(http, options, authenticode, telemetry: telemetry);

        var error = await Assert.ThrowsAsync<UpdateSecurityException>(() => service.DownloadAsync(verified));

        Assert.Equal(expected, error.Reason);
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.UpdateDirectory, "*.partial"));
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.UpdateDirectory, "*.msi"));
        Assert.Equal(UpdateEventKindV1.Failed, Assert.Single(telemetry.Events).Kind);
    }

    [Fact]
    public async Task Installed_update_is_reported_only_after_the_new_version_starts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var environment = new TempEnvironment();
        var options = CreateOptions(key) with { UpdateDirectory = environment.Paths.UpdateDirectory };
        Directory.CreateDirectory(options.UpdateDirectory);
        var eventId = Guid.NewGuid();
        var markerPath = Path.Combine(options.UpdateDirectory, "pending-install.json");
        await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(new
        {
            EventId = eventId,
            Version = options.CurrentVersion.ToString(),
            CreatedAtUtc = DateTimeOffset.UtcNow,
        }));
        var telemetry = new RecordingUpdateEventSink();
        using var http = new HttpClient(new StaticContentHandler([]));
        var service = new UpdateService(
            http,
            options,
            new FixedAuthenticodeVerifier(true, new string('A', 64)),
            telemetry: telemetry);

        await service.ReportConfirmedInstallationAsync();

        var installed = Assert.Single(telemetry.Events);
        Assert.Equal(eventId, installed.EventId);
        Assert.Equal(UpdateEventKindV1.Installed, installed.Kind);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task Package_download_rejects_even_same_host_redirects_away_from_the_signed_url()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var environment = new TempEnvironment();
        var packageBytes = Encoding.UTF8.GetBytes("signed-msi-test-payload");
        var fingerprint = new string('A', 64);
        var options = CreateOptions(key) with
        {
            UpdateDirectory = environment.Paths.UpdateDirectory,
            AllowedPublisherCertificateSha256 = new HashSet<string>(StringComparer.Ordinal) { fingerprint },
        };
        var descriptor = CreateManifest().Packages.Single() with
        {
            SizeBytes = packageBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(packageBytes)),
        };
        var verified = new VerifiedUpdate(
            CreateManifest() with { Packages = [descriptor] },
            descriptor,
            new Version(0, 6, 0, 0),
            new Version(0, 5, 0, 0));
        using var http = new HttpClient(new RedirectedContentHandler(
            packageBytes,
            new Uri("https://updates.peeronq.example/beta/redirected.msi")));
        var service = new UpdateService(http, options, new FixedAuthenticodeVerifier(true, fingerprint));

        var error = await Assert.ThrowsAsync<UpdateSecurityException>(() => service.DownloadAsync(verified));

        Assert.Equal(UpdateRejectionReason.UnexpectedRedirect, error.Reason);
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.UpdateDirectory, "*.partial"));
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.UpdateDirectory, "*.msi"));
    }

    [Fact]
    public async Task Installer_launch_rechecks_the_exact_signed_size_and_hash()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var environment = new TempEnvironment();
        var expectedBytes = Encoding.UTF8.GetBytes("verified-package");
        var tamperedBytes = Encoding.UTF8.GetBytes("tampered-package");
        Assert.Equal(expectedBytes.Length, tamperedBytes.Length);
        var fingerprint = new string('A', 64);
        var options = CreateOptions(key) with
        {
            UpdateDirectory = environment.Paths.UpdateDirectory,
            AllowedPublisherCertificateSha256 = new HashSet<string>(StringComparer.Ordinal) { fingerprint },
        };
        Directory.CreateDirectory(options.UpdateDirectory);
        var installerPath = Path.Combine(options.UpdateDirectory, "PeerOnQ-0.6.0-x64.msi");
        await File.WriteAllBytesAsync(installerPath, tamperedBytes);
        var descriptor = CreateManifest().Packages.Single() with
        {
            SizeBytes = expectedBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(expectedBytes)),
        };
        var update = new VerifiedUpdate(
            CreateManifest() with { Packages = [descriptor] },
            descriptor,
            new Version(0, 6, 0, 0),
            new Version(0, 5, 0, 0));
        using var http = new HttpClient(new StaticContentHandler([]));
        var service = new UpdateService(http, options, new FixedAuthenticodeVerifier(true, fingerprint));

        var error = await Assert.ThrowsAsync<UpdateSecurityException>(() =>
            service.LaunchInstallerAsync(new StagedUpdate(update, installerPath)));

        Assert.Equal(UpdateRejectionReason.HashMismatch, error.Reason);
        Assert.False(File.Exists(installerPath));
    }

    [Fact]
    public async Task Audit_chain_detects_tampering_and_redacts_identifiers_and_addresses()
    {
        using var environment = new TempEnvironment();
        var audit = new SqliteSecurityAuditLog(environment.Database, RandomNumberGenerator.GetBytes(32), "LNK-123-456-789-000", "0.5.0");
        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.SessionStarted,
            OccurredAt = DateTimeOffset.UtcNow,
            PeerMaskedId = "LNK-987-654-321-000",
            PermissionSet = SessionPermission.ViewScreen,
            Outcome = "peer 192.168.1.20 or [2001:db8::1] connected",
            SafeMetadata = new Dictionary<string, string>
            {
                ["reason"] = "from 10.0.0.4",
                ["token"] = "never",
                ["clipboard"] = "never-store-this-content",
            },
        });

        var stored = Assert.Single(await audit.ReadRecentAsync(10));
        Assert.DoesNotContain("456-789", stored.LocalDevice, StringComparison.Ordinal);
        Assert.DoesNotContain("654-321", stored.PeerMaskedId, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.1.20", stored.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::1", stored.Outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.4", stored.SafeMetadata["reason"], StringComparison.Ordinal);
        Assert.DoesNotContain("token", stored.SafeMetadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("clipboard", stored.SafeMetadata.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.True((await audit.VerifyIntegrityAsync()).IsValid);

        using var connection = environment.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE security_audit SET outcome = 'tampered' WHERE event_id = $id;";
        command.Parameters.AddWithValue("$id", stored.EventId.ToString("D"));
        command.ExecuteNonQuery();

        Assert.False((await audit.VerifyIntegrityAsync()).IsValid);
    }

    [Fact]
    public async Task Crash_reporting_is_opt_in_and_stores_no_exception_content()
    {
        using var environment = new TempEnvironment();
        var audit = new SqliteSecurityAuditLog(environment.Database, RandomNumberGenerator.GetBytes(32));
        var privacy = new PrivacySettingsService(new DpapiSecretStore(environment.Paths.SecretsDirectory), audit);
        var reporter = new CrashReportService(environment.Paths.CrashReportDirectory, privacy, audit);
        var exception = new InvalidOperationException("secret-token and C:\\Users\\Pasha\\private.txt");

        Assert.Null(await reporter.RecordAsync(exception, "test"));
        Assert.Empty(Directory.EnumerateFiles(environment.Paths.CrashReportDirectory));

        await privacy.SetCrashReportingEnabledAsync(true);
        var reportPath = await reporter.RecordAsync(exception, "test");
        var report = await File.ReadAllTextAsync(Assert.IsType<string>(reportPath));
        Assert.DoesNotContain("secret-token", report, StringComparison.Ordinal);
        Assert.DoesNotContain("private.txt", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Pasha", report, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, report, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_logs_redact_sensitive_properties_addresses_paths_ids_and_exception_content()
    {
        using var environment = new TempEnvironment();
        var logger = PeerOnQLogging.CreateSerilogLogger(
            environment.Paths.LogDirectory,
            appVersion: "0.9.1.0");
        try
        {
            logger.Information(
                "Remote event {DeviceId} {Address} {FilePath} {AccessToken}",
                "LNK-123-456-789-000",
                "192.168.10.44",
                @"C:\Users\Pasha\private.txt",
                "plain-secret-token-value");
            logger.Error(
                new InvalidOperationException("exception-secret at C:\\Users\\Pasha\\secret.txt"),
                "Operation failed for {PeerId}",
                "987-654-321-000");
        }
        finally
        {
            (logger as IDisposable)?.Dispose();
        }

        var logPath = Assert.Single(Directory.EnumerateFiles(environment.Paths.LogDirectory, "peeronq-*.log"));
        var log = File.ReadAllText(logPath);

        Assert.DoesNotContain("123-456-789-000", log, StringComparison.Ordinal);
        Assert.DoesNotContain("987-654-321-000", log, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.44", log, StringComparison.Ordinal);
        Assert.DoesNotContain("Pasha", log, StringComparison.Ordinal);
        Assert.DoesNotContain("private.txt", log, StringComparison.Ordinal);
        Assert.DoesNotContain("plain-secret-token-value", log, StringComparison.Ordinal);
        Assert.DoesNotContain("exception-secret", log, StringComparison.Ordinal);
        Assert.Contains(typeof(InvalidOperationException).FullName!, log, StringComparison.Ordinal);
        Assert.Contains("\"AppVersion\":\"0.9.1.0\"", log, StringComparison.Ordinal);
        Assert.All(File.ReadLines(logPath), line => Assert.NotNull(JsonDocument.Parse(line)));
    }

    [Fact]
    public async Task Audit_retention_rechains_remaining_records()
    {
        using var environment = new TempEnvironment();
        var audit = new SqliteSecurityAuditLog(environment.Database, RandomNumberGenerator.GetBytes(32));
        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.SessionEnded,
            OccurredAt = DateTimeOffset.UtcNow.AddDays(-120),
            Outcome = "old",
        });
        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.SessionStarted,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = "current",
        });

        await audit.ApplyRetentionAsync(TimeSpan.FromDays(90));

        Assert.Equal("current", Assert.Single(await audit.ReadRecentAsync(10)).Outcome);
        Assert.True((await audit.VerifyIntegrityAsync()).IsValid);
    }

    private static UpdateClientOptions CreateOptions(ECDsa key) => new()
    {
        ManifestUri = new Uri("https://updates.peeronq.example/beta/manifest.json"),
        PublicKeySpkiBase64 = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
        KeyId = "release-test-1",
        AllowedPublisherCertificateSha256 = new HashSet<string>(StringComparer.Ordinal) { new string('A', 64) },
        UpdateDirectory = Path.Combine(Path.GetTempPath(), "peeronq-update-test"),
        DeviceRolloutId = "device-test-1",
        CurrentVersion = new Version(0, 5, 1, 0),
        Channel = UpdateChannel.Beta,
        Architecture = "x64",
    };

    private static UpdateManifest CreateManifest() => new()
    {
        SchemaVersion = 1,
        ProductId = UpdateClientOptions.DefaultProductId,
        Version = "0.6.0.0",
        MinimumSupportedVersion = "0.5.0.0",
        Channel = UpdateChannel.Beta,
        IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
        RolloutPercentage = 100,
        RolloutSeed = "rollout-1",
        Packages =
        [
            new UpdatePackageDescriptor
            {
                Architecture = "x64",
                Url = "https://updates.peeronq.example/beta/PeerOnQ-x64.msi",
                Sha256 = new string('0', 64),
                SizeBytes = 1,
            },
        ],
    };

    private static byte[] Sign(UpdateManifest manifest, ECDsa key, string keyId)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(manifest, WebJson);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateEnvelope
        {
            SchemaVersion = 1,
            KeyId = keyId,
            Payload = Convert.ToBase64String(payload),
            Signature = Convert.ToBase64String(signature),
        }, WebJson);
    }

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private sealed class FixedAuthenticodeVerifier(bool trusted, string fingerprint) : IAuthenticodeVerifier
    {
        public FileSignatureVerification Verify(string filePath) => new(trusted, fingerprint, trusted ? null : "invalid");
    }

    private sealed class RecordingUpdateEventSink : IUpdateEventSink
    {
        public List<ClientUpdateEventV1> Events { get; } = [];

        public ValueTask EnqueueAsync(
            ClientUpdateEventV1 updateEvent,
            CancellationToken cancellationToken = default)
        {
            Events.Add(updateEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StaticContentHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class RedirectedContentHandler(byte[] content, Uri finalUri) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var finalRequest = new HttpRequestMessage(request.Method, finalUri);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = finalRequest,
                Content = new ByteArrayContent(content),
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }
}
