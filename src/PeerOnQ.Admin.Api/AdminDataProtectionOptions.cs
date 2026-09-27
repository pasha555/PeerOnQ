using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerOnQ.Admin.Api;

public sealed class AdminDataProtectionOptions
{
    public const string SectionName = "PeerOnQ:AdminDataProtection";

    public string? CertificatePath { get; set; }
    public string? CertificatePasswordFile { get; set; }
    public bool AllowUnprotectedKeysForDevelopment { get; set; }
}

internal static class AdminDataProtectionConfiguration
{
    internal static void EnsureUnprotectedModeAllowed(
        IHostEnvironment environment,
        AdminDataProtectionOptions options)
    {
        if ((environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            && options.AllowUnprotectedKeysForDevelopment)
        {
            return;
        }

        throw new InvalidOperationException(
            "PeerOnQ:AdminDataProtection:CertificatePath is required outside an explicitly opted-in development environment.");
    }

    internal static X509Certificate2 LoadCertificate(AdminDataProtectionOptions options)
    {
        var certificatePath = Path.GetFullPath(options.CertificatePath!);
        if (!File.Exists(certificatePath))
            throw new InvalidOperationException("The configured admin data-protection certificate file does not exist.");
        if (string.IsNullOrWhiteSpace(options.CertificatePasswordFile))
            throw new InvalidOperationException("PeerOnQ:AdminDataProtection:CertificatePasswordFile is required when a certificate is configured.");

        var passwordPath = Path.GetFullPath(options.CertificatePasswordFile);
        if (!File.Exists(passwordPath))
            throw new InvalidOperationException("The configured admin data-protection certificate password file does not exist.");
        var passwordInfo = new FileInfo(passwordPath);
        if (passwordInfo.Length is <= 0 or > 4096)
            throw new InvalidOperationException("The admin data-protection certificate password secret has an invalid size.");

        var password = File.ReadAllText(passwordPath).TrimEnd('\r', '\n');
        if (password.Length is < 16 or > 1024)
            throw new InvalidOperationException("The admin data-protection certificate password secret has an invalid length.");

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(
                certificatePath,
                password,
                X509KeyStorageFlags.EphemeralKeySet);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException("The admin data-protection certificate must contain a private key.");
            }

            var now = DateTimeOffset.UtcNow;
            if (certificate.NotBefore.ToUniversalTime() > now || certificate.NotAfter.ToUniversalTime() <= now.AddDays(7))
            {
                certificate.Dispose();
                throw new InvalidOperationException("The admin data-protection certificate is not currently valid for a safe deployment window.");
            }

            return certificate;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("The admin data-protection certificate could not be loaded.", exception);
        }
    }
}
