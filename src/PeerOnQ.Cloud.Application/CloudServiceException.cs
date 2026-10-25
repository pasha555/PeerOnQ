namespace PeerOnQ.Cloud.Application;

public sealed class CloudServiceException : Exception
{
    public CloudServiceException(
        string code,
        string message,
        bool isPermanent = true,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsPermanent = isPermanent;
    }

    public string Code { get; }
    public bool IsPermanent { get; }
}

public static class CloudErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string UnsupportedProtocol = "UNSUPPORTED_PROTOCOL";
    public const string UnsupportedVersion = "UNSUPPORTED_VERSION";
    public const string InstallationNotFound = "INSTALLATION_NOT_FOUND";
    public const string InstallationBlocked = "INSTALLATION_BLOCKED";
    public const string InstallationIdentityConflict = "INSTALLATION_IDENTITY_CONFLICT";
    public const string ChallengeInvalidOrExpired = "CHALLENGE_INVALID_OR_EXPIRED";
    public const string IdentityProofInvalid = "IDENTITY_PROOF_INVALID";
    public const string IdentityFingerprintMismatch = "IDENTITY_FINGERPRINT_MISMATCH";
    public const string DeviceRevoked = "DEVICE_REVOKED";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string DownloadNotFound = "DOWNLOAD_NOT_FOUND";
    public const string DiagnosticConsentRequired = "DIAGNOSTIC_CONSENT_REQUIRED";
    public const string DiagnosticNotFound = "DIAGNOSTIC_NOT_FOUND";
    public const string DiagnosticUploadRejected = "DIAGNOSTIC_UPLOAD_REJECTED";
}
