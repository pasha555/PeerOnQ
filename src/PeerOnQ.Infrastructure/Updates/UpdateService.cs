using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Infrastructure.Updates;

public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly UpdateClientOptions _options;
    private readonly UpdateManifestVerifier _verifier;
    private readonly IAuthenticodeVerifier _authenticode;
    private readonly ISecurityAuditLog? _audit;
    private readonly IUpdateEventSink? _telemetry;

    public UpdateService(
        HttpClient httpClient,
        UpdateClientOptions options,
        IAuthenticodeVerifier authenticodeVerifier,
        ISecurityAuditLog? audit = null,
        UpdateManifestVerifier? manifestVerifier = null,
        IUpdateEventSink? telemetry = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _authenticode = authenticodeVerifier ?? throw new ArgumentNullException(nameof(authenticodeVerifier));
        _audit = audit;
        _telemetry = telemetry;
        _verifier = manifestVerifier ?? new UpdateManifestVerifier();
        _http.Timeout = options.HttpTimeout;
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.ManifestUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            EnsureTrustedResponse(response, _options.ManifestUri);

            var envelope = await ReadBoundedAsync(
                response.Content,
                _options.MaximumManifestBytes,
                cancellationToken);
            var verified = _verifier.Verify(envelope, _options);
            var status = _verifier.Classify(verified, _options);
            await AuditAsync(SecurityAuditEventType.UpdateCheckCompleted, "accepted", verified.Version.ToString());
            if (status is UpdateCheckStatus.Available or UpdateCheckStatus.Required)
                await EnqueueTelemetryAsync(UpdateEventKindV1.Offered, verified.Version, failureCode: null);
            return new UpdateCheckResult(status, verified);
        }
        catch (UpdateSecurityException ex)
        {
            await AuditAsync(SecurityAuditEventType.UpdateRejected, ex.Reason.ToString(), null);
            return new UpdateCheckResult(UpdateCheckStatus.Rejected, RejectionReason: ex.Reason, Detail: ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            await AuditAsync(SecurityAuditEventType.UpdateCheckFailed, "network_failure", null);
            return new UpdateCheckResult(
                UpdateCheckStatus.Rejected,
                RejectionReason: UpdateRejectionReason.NetworkFailure,
                Detail: "The update service could not be reached securely.");
        }
    }

    public async Task<StagedUpdate> DownloadAsync(
        VerifiedUpdate update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var packageUri = new Uri(update.Package.Url, UriKind.Absolute);
        Directory.CreateDirectory(_options.UpdateDirectory);

        var finalPath = Path.Combine(
            _options.UpdateDirectory,
            $"PeerOnQ-{update.Version}-{update.Package.Architecture}.msi");
        var partialPath = finalPath + ".partial";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            EnsureTrustedResponse(response, packageUri);

            if (response.Content.Headers.ContentLength is { } contentLength
                && contentLength != update.Package.SizeBytes)
            {
                throw new UpdateSecurityException(
                    contentLength > update.Package.SizeBytes
                        ? UpdateRejectionReason.DownloadTooLarge
                        : UpdateRejectionReason.HashMismatch,
                    "The update package length does not match the signed manifest.");
            }

            long received = 0;
            string actualHash;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(
                             partialPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    received += read;
                    if (received > update.Package.SizeBytes || received > _options.MaximumPackageBytes)
                        throw new UpdateSecurityException(UpdateRejectionReason.DownloadTooLarge, "The update package exceeded its signed size.");

                    hash.AppendData(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(new UpdateDownloadProgress(received, update.Package.SizeBytes));
                }

                await destination.FlushAsync(cancellationToken);
                actualHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            if (received != update.Package.SizeBytes)
                throw new UpdateSecurityException(UpdateRejectionReason.HashMismatch, "The update package is incomplete.");

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(update.Package.Sha256)))
            {
                throw new UpdateSecurityException(UpdateRejectionReason.HashMismatch, "The update package hash does not match the signed manifest.");
            }

            var signature = _authenticode.Verify(partialPath);
            if (!signature.IsTrusted)
                throw new UpdateSecurityException(UpdateRejectionReason.InvalidAuthenticodeSignature, "The update installer has no valid Authenticode trust chain.");

            var publisher = NormalizeFingerprint(signature.CertificateSha256);
            if (publisher is null || !_options.AllowedPublisherCertificateSha256.Contains(publisher))
                throw new UpdateSecurityException(UpdateRejectionReason.WrongPublisher, "The update installer publisher is not trusted.");

            File.Move(partialPath, finalPath, overwrite: true);
            await AuditAsync(SecurityAuditEventType.UpdateDownloaded, "verified", update.Version.ToString());
            await EnqueueTelemetryAsync(UpdateEventKindV1.Downloaded, update.Version, failureCode: null);
            return new StagedUpdate(update, finalPath);
        }
        catch (UpdateSecurityException ex)
        {
            TryDelete(partialPath);
            await AuditAsync(SecurityAuditEventType.UpdateRejected, ex.Reason.ToString(), update.Version.ToString());
            await EnqueueTelemetryAsync(UpdateEventKindV1.Failed, update.Version, ex.Reason.ToString());
            throw;
        }
        catch
        {
            TryDelete(partialPath);
            await EnqueueTelemetryAsync(UpdateEventKindV1.Failed, update.Version, "download_failed");
            throw;
        }
    }

    public async Task LaunchInstallerAsync(StagedUpdate stagedUpdate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagedUpdate);
        cancellationToken.ThrowIfCancellationRequested();

        var fullPath = Path.GetFullPath(stagedUpdate.InstallerPath);
        var updateRoot = Path.GetFullPath(_options.UpdateDirectory).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(updateRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || !string.Equals(Path.GetExtension(fullPath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidPackage, "The staged installer path is invalid.");
        }

        var info = new FileInfo(fullPath);
        if (info.Length != stagedUpdate.Update.Package.SizeBytes)
        {
            TryDelete(fullPath);
            throw new UpdateSecurityException(UpdateRejectionReason.HashMismatch, "The staged installer size changed after download verification.");
        }

        string stagedHash;
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            stagedHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(stagedHash),
                Convert.FromHexString(stagedUpdate.Update.Package.Sha256)))
        {
            TryDelete(fullPath);
            throw new UpdateSecurityException(UpdateRejectionReason.HashMismatch, "The staged installer hash changed after download verification.");
        }

        var signature = _authenticode.Verify(fullPath);
        var publisher = NormalizeFingerprint(signature.CertificateSha256);
        if (!signature.IsTrusted || publisher is null || !_options.AllowedPublisherCertificateSha256.Contains(publisher))
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidAuthenticodeSignature, "The staged installer no longer has a trusted signature.");

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var msiexec = Path.Combine(systemDirectory, "msiexec.exe");
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = msiexec,
            ArgumentList = { "/i", fullPath },
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Windows Installer could not be started.");

        await WritePendingInstallMarkerAsync(stagedUpdate.Update.Version, CancellationToken.None);

        await AuditAsync(SecurityAuditEventType.UpdateInstallStarted, "user_confirmed", stagedUpdate.Update.Version.ToString());
    }

    public async Task ReportConfirmedInstallationAsync(CancellationToken cancellationToken = default)
    {
        var markerPath = PendingInstallMarkerPath;
        if (!File.Exists(markerPath)) return;

        PendingInstallMarker? marker;
        try
        {
            var info = new FileInfo(markerPath);
            if (info.Length is <= 0 or > 16 * 1024) throw new InvalidDataException("Invalid update marker size.");
            await using var stream = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            marker = await JsonSerializer.DeserializeAsync<PendingInstallMarker>(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryDelete(markerPath);
            return;
        }

        if (marker is null || marker.EventId == Guid.Empty || !Version.TryParse(marker.Version, out var target))
        {
            TryDelete(markerPath);
            return;
        }

        if (!SameProductVersion(_options.CurrentVersion, target)) return;
        var queued = await EnqueueTelemetryAsync(
            UpdateEventKindV1.Installed,
            target,
            failureCode: null,
            eventId: marker.EventId,
            cancellationToken);
        if (queued) TryDelete(markerPath);
    }

    private static void EnsureTrustedResponse(HttpResponseMessage response, Uri expectedUri)
    {
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateSecurityException(UpdateRejectionReason.InsecureTransport, "The update request was redirected away from HTTPS.");
        if (!finalUri.Equals(expectedUri))
            throw new UpdateSecurityException(UpdateRejectionReason.UnexpectedRedirect, "The update request did not finish at the exact signed URL.");
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 and var declared && declared > maximumBytes)
            throw new UpdateSecurityException(UpdateRejectionReason.InvalidEnvelope, "The update manifest is too large.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(Math.Min(maximumBytes, 16 * 1024));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new UpdateSecurityException(UpdateRejectionReason.InvalidEnvelope, "The update manifest is too large.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private Task AuditAsync(SecurityAuditEventType type, string outcome, string? version) =>
        _audit?.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = type,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = outcome,
            IntegrityMetadata = version is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal) { ["update_version"] = version },
        }) ?? Task.CompletedTask;

    private async Task<bool> EnqueueTelemetryAsync(
        UpdateEventKindV1 kind,
        Version version,
        string? failureCode,
        Guid? eventId = null,
        CancellationToken cancellationToken = default)
    {
        if (_telemetry is null) return false;
        try
        {
            await _telemetry.EnqueueAsync(
                new ClientUpdateEventV1(
                    eventId ?? Guid.NewGuid(),
                    kind,
                    version.ToString(),
                    _options.Channel == UpdateChannel.Stable ? InstallChannelV1.Stable : InstallChannelV1.Beta,
                    ArchitectureOf(_options.Architecture),
                    failureCode is null ? null : LimitFailureCode(failureCode),
                    DateTimeOffset.UtcNow),
                cancellationToken);
            return true;
        }
        catch
        {
            // Analytics must never change signature, hash, publisher, rollout, or install decisions.
            return false;
        }
    }

    private async Task WritePendingInstallMarkerAsync(Version version, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.UpdateDirectory);
        var temporaryPath = PendingInstallMarkerPath + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new PendingInstallMarker(Guid.NewGuid(), version.ToString(), DateTimeOffset.UtcNow),
                    cancellationToken: cancellationToken);
            }

            File.Move(temporaryPath, PendingInstallMarkerPath, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            // A local analytics marker must not stop a verified installer from launching.
        }
    }

    private string PendingInstallMarkerPath => Path.Combine(_options.UpdateDirectory, "pending-install.json");

    private static ArchitectureKindV1 ArchitectureOf(string value) => value.ToLowerInvariant() switch
    {
        "x64" => ArchitectureKindV1.X64,
        "arm64" => ArchitectureKindV1.Arm64,
        "x86" => ArchitectureKindV1.X86,
        "arm" => ArchitectureKindV1.Arm,
        _ => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ArchitectureKindV1.Arm64
            : ArchitectureKindV1.X64,
    };

    private static bool SameProductVersion(Version left, Version right) =>
        left.Major == right.Major && left.Minor == right.Minor && left.Build == right.Build;

    private static string LimitFailureCode(string value)
    {
        var result = new string(value
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            .Take(64)
            .ToArray());
        return string.IsNullOrEmpty(result) ? "update_failed" : result;
    }

    private static string? NormalizeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return normalized.Length == 64 ? normalized : null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // The partial path remains inside the private update directory and is never executed.
        }
    }

    private sealed record PendingInstallMarker(Guid EventId, string Version, DateTimeOffset CreatedAtUtc);
}
