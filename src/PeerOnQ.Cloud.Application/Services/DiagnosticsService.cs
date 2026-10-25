using System.Security.Cryptography;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Cloud.Application.Security;
using PeerOnQ.Cloud.Domain;
using PeerOnQ.Cloud.Domain.Entities;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Application.Services;

public sealed class DiagnosticsService(
    IDiagnosticRepository diagnostics,
    IInstallationRepository installations,
    ICloudUnitOfWork unitOfWork,
    CloudSecurityOptions securityOptions,
    RetentionOptions retentionOptions,
    TimeProvider? timeProvider = null) : IDiagnosticsService
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "sanitized-logs",
        "app-metadata",
        "webrtc-statistics-summary",
        "connection-failure-codes",
        "update-status",
        "database-schema-version",
        "client-health-checks"
    };

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DiagnosticCreateResultV1> CreateRequestAsync(DeviceAccessPrincipal caller, DiagnosticCreateRequestV1 request, CancellationToken cancellationToken = default)
    {
        securityOptions.Validate();
        retentionOptions.Validate(securityOptions.DiagnosticUploadTokenLifetime);
        if (!request.ConsentGranted)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticConsentRequired, "Explicit diagnostics consent is required.");
        if (request.IncludedCategories.Count is 0 or > 16 || request.IncludedCategories.Any(category => !AllowedCategories.Contains(category)))
            throw new CloudServiceException(CloudErrorCodes.InvalidRequest, "Diagnostics include an unsupported data category.");
        if (caller.InstallationId != request.InstallationId)
            throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid, "Authenticated principal does not own the diagnostic request.");
        var installation = await installations.FindByIdAsync(request.InstallationId, cancellationToken);
        if (installation is null)
            throw new CloudServiceException(CloudErrorCodes.InstallationNotFound, "Installation not found.");
        if (installation.DeviceId != caller.DeviceId)
            throw new CloudServiceException(CloudErrorCodes.IdentityProofInvalid, "Authenticated device does not own the installation.");

        var now = _time.GetUtcNow();
        var rawToken = RandomNumberGenerator.GetBytes(32);
        var token = CanonicalDeviceChallenge.Base64Url(rawToken);
        var bundle = DiagnosticBundle.Create(
            request.InstallationId,
            SHA256.HashData(rawToken),
            now + securityOptions.DiagnosticUploadTokenLifetime,
            now + retentionOptions.Diagnostics,
            request.AppVersion,
            request.OsVersion,
            request.Architecture.ToDomain(),
            request.ErrorId,
            request.IssueCategory,
            request.DatabaseSchemaVersion,
            now);
        diagnostics.Add(bundle);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return new DiagnosticCreateResultV1(bundle.Id, token, bundle.UploadTokenExpiresAtUtc, bundle.ExpiresAtUtc);
    }

    public async Task<DiagnosticStatusResponseV1> CompleteUploadAsync(DeviceAccessPrincipal caller, DiagnosticUploadCompleteRequestV1 request, CancellationToken cancellationToken = default)
    {
        if (request.SanitizedArchiveSizeBytes <= 0 || request.SanitizedArchiveSizeBytes > securityOptions.MaximumDiagnosticArchiveBytes)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive size is invalid.");
        var bundle = await diagnostics.FindByIdAsync(request.DiagnosticId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.DiagnosticNotFound, "Diagnostic request not found.");
        EnsureOwner(caller, bundle);

        byte[] rawToken;
        byte[] archiveHash;
        try
        {
            rawToken = DecodeBase64Url(request.UploadToken);
            archiveHash = Convert.FromBase64String(request.Sha256Base64);
        }
        catch (FormatException)
        {
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic upload proof is malformed.");
        }
        if (rawToken.Length != 32 || archiveHash.Length != 32)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic upload proof is malformed.");

        var now = _time.GetUtcNow();
        var objectKey = $"diagnostics/{bundle.Id:N}.zip";
        var reference = $"D-{Convert.ToHexString(RandomNumberGenerator.GetBytes(6))}";
        try
        {
            bundle.MarkUploaded(SHA256.HashData(rawToken), request.SanitizedArchiveSizeBytes,
                Convert.ToHexString(archiveHash).ToLowerInvariant(), objectKey, reference, now);
        }
        catch (InvalidOperationException)
        {
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic upload authorization is invalid or expired.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Map(bundle);
    }

    public async Task<DiagnosticStatusResponseV1> GetStatusAsync(DeviceAccessPrincipal caller, Guid diagnosticId, CancellationToken cancellationToken = default)
    {
        var bundle = await diagnostics.FindByIdAsync(diagnosticId, cancellationToken)
            ?? throw new CloudServiceException(CloudErrorCodes.DiagnosticNotFound, "Diagnostic request not found.");
        EnsureOwner(caller, bundle);
        return Map(bundle);
    }

    private static void EnsureOwner(DeviceAccessPrincipal caller, DiagnosticBundle bundle)
    {
        if (bundle.InstallationId != caller.InstallationId)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticNotFound, "Diagnostic request not found.");
    }

    private static DiagnosticStatusResponseV1 Map(DiagnosticBundle bundle) =>
        new(bundle.Id, bundle.Status switch
        {
            DiagnosticStatus.AwaitingUpload => DiagnosticStatusV1.AwaitingUpload,
            DiagnosticStatus.Uploaded => DiagnosticStatusV1.Uploaded,
            DiagnosticStatus.Processing => DiagnosticStatusV1.Processing,
            DiagnosticStatus.Available => DiagnosticStatusV1.Available,
            DiagnosticStatus.Rejected => DiagnosticStatusV1.Rejected,
            DiagnosticStatus.Deleting => DiagnosticStatusV1.Deleting,
            DiagnosticStatus.Expired => DiagnosticStatusV1.Expired,
            DiagnosticStatus.Deleted => DiagnosticStatusV1.Deleted,
            _ => throw new InvalidOperationException("Unknown diagnostic state.")
        }, bundle.ConsentGranted, bundle.CreatedAtUtc, bundle.ExpiresAtUtc, bundle.ReferenceCode);

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
        return Convert.FromBase64String(normalized);
    }
}
