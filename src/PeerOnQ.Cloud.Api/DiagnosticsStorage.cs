using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PeerOnQ.Cloud.Application;
using PeerOnQ.Cloud.Application.Abstractions;
using PeerOnQ.Shared.Contracts.V1;

namespace PeerOnQ.Cloud.Api;

public sealed class DiagnosticStorageOptions
{
    public const string SectionName = "PeerOnQ:DiagnosticsStorage";
    public const long MaximumArchiveBytes = 20L * 1024 * 1024;

    public string Provider { get; set; } = string.Empty;
    public string? FileSystemPath { get; set; }
    public Uri? HttpEndpoint { get; set; }
    public string? HttpBearerToken { get; set; }
    public string[] HttpAllowedHosts { get; set; } = [];
    public bool AllowFileSystemOutsideDevelopment { get; set; }
}

public sealed class DiagnosticStorageOptionsValidator(IHostEnvironment environment)
    : IValidateOptions<DiagnosticStorageOptions>
{
    public ValidateOptionsResult Validate(string? name, DiagnosticStorageOptions options)
    {
        if (options.Provider.Equals("FileSystem", StringComparison.OrdinalIgnoreCase))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing")
                && !options.AllowFileSystemOutsideDevelopment)
                return ValidateOptionsResult.Fail("FileSystem diagnostics storage outside Development requires an explicit single-node opt-in.");
            if (string.IsNullOrWhiteSpace(options.FileSystemPath))
                return ValidateOptionsResult.Fail("FileSystemPath is required for FileSystem diagnostics storage.");
            return ValidateOptionsResult.Success;
        }

        if (options.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase))
        {
            if (options.HttpEndpoint is null || !options.HttpEndpoint.IsAbsoluteUri || options.HttpEndpoint.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(options.HttpEndpoint.UserInfo)
                || !string.IsNullOrEmpty(options.HttpEndpoint.Query)
                || !string.IsNullOrEmpty(options.HttpEndpoint.Fragment))
                return ValidateOptionsResult.Fail("An absolute HTTPS HttpEndpoint is required for HTTP diagnostics storage.");
            if (options.HttpAllowedHosts.Length == 0
                || !options.HttpAllowedHosts.Contains(options.HttpEndpoint.IdnHost, StringComparer.OrdinalIgnoreCase))
                return ValidateOptionsResult.Fail("The HTTP diagnostics storage host must be explicitly allowlisted.");
            if (string.IsNullOrWhiteSpace(options.HttpBearerToken) || options.HttpBearerToken.Length < 32)
                return ValidateOptionsResult.Fail("The HTTP diagnostics storage credential must contain at least 32 characters.");
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail("Diagnostics storage Provider must be FileSystem or Http.");
    }
}

public interface IDiagnosticBlobStore : IDiagnosticBlobLifecycleStore
{
    Task StoreAsync(Guid diagnosticId, string verifiedArchivePath, long sizeBytes, CancellationToken cancellationToken);
}

internal static class DiagnosticObjectKey
{
    public static string For(Guid diagnosticId) => $"diagnostics/{diagnosticId:N}.zip";

    public static void Validate(Guid diagnosticId, string? objectKey)
    {
        if (objectKey is not null && !string.Equals(objectKey, For(diagnosticId), StringComparison.Ordinal))
            throw new InvalidOperationException("The diagnostic storage object key does not match the diagnostic identifier.");
    }
}

public sealed class FileSystemDiagnosticBlobStore(IOptions<DiagnosticStorageOptions> options) : IDiagnosticBlobStore
{
    public async Task StoreAsync(Guid diagnosticId, string verifiedArchivePath, long sizeBytes, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(options.Value.FileSystemPath!);
        var directory = Path.Combine(root, "diagnostics");
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"{diagnosticId:N}.zip");
        var partial = Path.Combine(directory, $".{diagnosticId:N}.{Guid.NewGuid():N}.partial");

        try
        {
            await using (var source = new FileStream(verifiedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(target, 64 * 1024, cancellationToken);
                await target.FlushAsync(cancellationToken);
            }

            if (new FileInfo(partial).Length != sizeBytes)
                throw new IOException("The persisted diagnostic archive size did not match the verified upload.");
            File.Move(partial, destination, overwrite: false);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    public Task DeleteAsync(Guid diagnosticId, string? objectKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DiagnosticObjectKey.Validate(diagnosticId, objectKey);
        var root = Path.GetFullPath(options.Value.FileSystemPath!);
        var path = Path.Combine(root, "diagnostics", $"{diagnosticId:N}.zip");
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class HttpDiagnosticBlobStore(
    IHttpClientFactory clients,
    IOptions<DiagnosticStorageOptions> options) : IDiagnosticBlobStore
{
    public async Task StoreAsync(Guid diagnosticId, string verifiedArchivePath, long sizeBytes, CancellationToken cancellationToken)
    {
        await using var archive = new FileStream(verifiedArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var request = CreateRequest(HttpMethod.Put, diagnosticId);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Content = new StreamContent(archive);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        request.Content.Headers.ContentLength = sizeBytes;

        using var response = await clients.CreateClient("diagnostic-storage")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed || response.StatusCode == HttpStatusCode.Conflict)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "The diagnostic archive already exists.");
        if (!response.IsSuccessStatusCode)
            throw new CloudServiceException("DIAGNOSTIC_STORAGE_UNAVAILABLE", "Diagnostic storage is unavailable.", isPermanent: false);
    }

    public async Task DeleteAsync(Guid diagnosticId, string? objectKey, CancellationToken cancellationToken)
    {
        DiagnosticObjectKey.Validate(diagnosticId, objectKey);
        using var request = CreateRequest(HttpMethod.Delete, diagnosticId);
        using var response = await clients.CreateClient("diagnostic-storage")
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
            throw new CloudServiceException("DIAGNOSTIC_STORAGE_UNAVAILABLE", "Diagnostic storage cleanup failed.", isPermanent: false);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, Guid diagnosticId)
    {
        var configured = options.Value;
        var request = new HttpRequestMessage(method, new Uri(configured.HttpEndpoint!, DiagnosticObjectKey.For(diagnosticId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configured.HttpBearerToken);
        return request;
    }
}

public sealed class DiagnosticUploadProcessor(
    IDiagnosticBlobStore blobStore,
    IDiagnosticsService diagnosticsService)
{
    public async Task<DiagnosticStatusResponseV1> ProcessAsync(
        DeviceAccessPrincipal caller,
        Guid diagnosticId,
        string uploadToken,
        string expectedSha256Base64,
        IFormFile archive,
        CancellationToken cancellationToken)
    {
        if (diagnosticId == Guid.Empty || !IsValidUploadToken(uploadToken))
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic upload metadata is invalid.");
        if (archive.Length is <= 0 or > DiagnosticStorageOptions.MaximumArchiveBytes)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive size is invalid.");

        if (expectedSha256Base64.Length != 44)
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive hash is invalid.");
        byte[] expectedHash;
        try { expectedHash = Convert.FromBase64String(expectedSha256Base64); }
        catch (FormatException) { throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive hash is invalid."); }
        if (expectedHash.Length != 32
            || !string.Equals(Convert.ToBase64String(expectedHash), expectedSha256Base64, StringComparison.Ordinal))
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive hash is invalid.");

        var ingressRoot = Path.Combine(Path.GetTempPath(), "peeronq-diagnostic-ingress");
        Directory.CreateDirectory(ingressRoot);
        var temporaryPath = Path.Combine(ingressRoot, $"{diagnosticId:N}-{Guid.NewGuid():N}.partial");
        try
        {
            var actualHash = await CopyAndHashAsync(archive, temporaryPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
                throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive integrity validation failed.");

            await DiagnosticArchiveValidator.ValidateAsync(temporaryPath, cancellationToken);
            var verifiedLength = new FileInfo(temporaryPath).Length;
            var verifiedSha256Base64 = Convert.ToBase64String(actualHash);
            await blobStore.StoreAsync(diagnosticId, temporaryPath, verifiedLength, cancellationToken);
            try
            {
                return await diagnosticsService.CompleteUploadAsync(
                    caller,
                    new DiagnosticUploadCompleteRequestV1(diagnosticId, uploadToken, verifiedLength, verifiedSha256Base64),
                    cancellationToken);
            }
            catch
            {
                await blobStore.DeleteAsync(diagnosticId, null, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsValidUploadToken(string value) =>
        value.Length == 43
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static async Task<byte[]> CopyAndHashAsync(IFormFile archive, string temporaryPath, CancellationToken cancellationToken)
    {
        await using var source = archive.OpenReadStream();
        await using var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        var header = new byte[4];
        var headerLength = 0;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > DiagnosticStorageOptions.MaximumArchiveBytes)
                throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Diagnostic archive exceeds the upload limit.");
            if (headerLength < header.Length)
            {
                var copy = Math.Min(header.Length - headerLength, read);
                buffer.AsSpan(0, copy).CopyTo(header.AsSpan(headerLength));
                headerLength += copy;
            }
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await target.FlushAsync(cancellationToken);
        if (total != archive.Length || headerLength != 4 || header[0] != (byte)'P' || header[1] != (byte)'K'
            || (header[2] != 3 && header[2] != 5 && header[2] != 7) || (header[3] != 4 && header[3] != 6 && header[3] != 8))
        {
            throw new CloudServiceException(CloudErrorCodes.DiagnosticUploadRejected, "Only a structurally valid ZIP archive is accepted.");
        }
        return hash.GetHashAndReset();
    }
}
