using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length == 2 && args[0] == "--attestation-key-pair")
{
    var keyOutputDirectory = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(keyOutputDirectory);

    using var attestationKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    File.WriteAllText(
        Path.Combine(keyOutputDirectory, "signaling-attestation-private.pem"),
        attestationKey.ExportPkcs8PrivateKeyPem());
    File.WriteAllText(
        Path.Combine(keyOutputDirectory, "signaling-attestation-public.pem"),
        attestationKey.ExportSubjectPublicKeyInfoPem());
    return 0;
}

if (args.Length is not (3 or 6))
{
    Console.Error.WriteLine(
        "Usage: dotnet run PeerOnQ.LocalCertificate.cs -- <output-directory> <pfx-password> <root-common-name> [signal-host turn-host bind-address] | --attestation-key-pair <output-directory>");
    return 64;
}

var outputDirectory = Path.GetFullPath(args[0]);
var pfxPassword = args[1];
if (pfxPassword.Length < 24)
{
    Console.Error.WriteLine("The local PFX password must contain at least 24 characters.");
    return 64;
}

Directory.CreateDirectory(outputDirectory);

var rootCommonName = args[2].Trim();
if (string.IsNullOrWhiteSpace(rootCommonName)
    || rootCommonName.IndexOfAny([',', '+', '"', '\\', '<', '>', ';']) >= 0)
{
    Console.Error.WriteLine("The local root common name is empty or contains an unsupported distinguished-name character.");
    return 64;
}

var signalHost = args.Length == 6 ? args[3] : "127.0.0.1";
var turnHost = args.Length == 6 ? args[4] : "127.0.0.1";
if (!IPAddress.TryParse(args.Length == 6 ? args[5] : "127.0.0.1", out var bindAddress)
    || bindAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
{
    Console.Error.WriteLine("The local bind address must be an IPv4 address.");
    return 64;
}
var now = DateTimeOffset.UtcNow;

using var rootKey = RSA.Create(3072);
var rootRequest = new CertificateRequest(
    $"CN={rootCommonName}",
    rootKey,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);
rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
    true));
rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));

using var rootCertificate = rootRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(5));

using var serverKey = RSA.Create(3072);
var serverRequest = new CertificateRequest(
    $"CN={signalHost}",
    serverKey,
    HashAlgorithmName.SHA256,
    RSASignaturePadding.Pkcs1);
serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
    true));
serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
    new OidCollection { new("1.3.6.1.5.5.7.3.1") },
    true));
serverRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(serverRequest.PublicKey, false));

var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
subjectAlternativeNames.AddDnsName(signalHost);
subjectAlternativeNames.AddDnsName(turnHost);
subjectAlternativeNames.AddDnsName("localhost");
subjectAlternativeNames.AddIpAddress(IPAddress.Loopback);
if (!bindAddress.Equals(IPAddress.Loopback)) subjectAlternativeNames.AddIpAddress(bindAddress);
serverRequest.CertificateExtensions.Add(subjectAlternativeNames.Build());

var serialNumber = RandomNumberGenerator.GetBytes(16);
using var issuedServerCertificate = serverRequest.Create(
    rootCertificate,
    now.AddMinutes(-5),
    now.AddDays(397),
    serialNumber);
using var serverCertificate = issuedServerCertificate.CopyWithPrivateKey(serverKey);

var certificateChain = new X509Certificate2Collection();
certificateChain.Add(serverCertificate);
certificateChain.Add(rootCertificate);
var pfx = certificateChain.Export(X509ContentType.Pkcs12, pfxPassword)
          ?? throw new InvalidOperationException("Failed to export the local PKCS#12 certificate.");

File.WriteAllBytes(Path.Combine(outputDirectory, "peeronq-local.pfx"), pfx);
File.WriteAllBytes(Path.Combine(outputDirectory, "root-ca.cer"), rootCertificate.Export(X509ContentType.Cert));
File.WriteAllText(
    Path.Combine(outputDirectory, "fullchain.pem"),
    serverCertificate.ExportCertificatePem() + Environment.NewLine + rootCertificate.ExportCertificatePem());
File.WriteAllText(Path.Combine(outputDirectory, "privkey.pem"), serverKey.ExportPkcs8PrivateKeyPem());
File.WriteAllText(
    Path.Combine(outputDirectory, "certificate.json"),
    $$"""
    {
      "signalHost": "{{signalHost}}",
      "turnHost": "{{turnHost}}",
      "bindAddress": "{{bindAddress}}",
      "rootSubject": "{{rootCertificate.Subject}}",
      "rootThumbprint": "{{rootCertificate.Thumbprint}}",
      "serverThumbprint": "{{serverCertificate.Thumbprint}}",
      "notBefore": "{{serverCertificate.NotBefore.ToUniversalTime():O}}",
      "notAfter": "{{serverCertificate.NotAfter.ToUniversalTime():O}}"
    }
    """);

Console.WriteLine(rootCertificate.Thumbprint);
return 0;
