using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

if (args.Length != 3
    || !File.Exists(args[0])
    || !Uri.TryCreate(args[1], UriKind.Absolute, out var endpoint)
    || endpoint.Scheme != Uri.UriSchemeHttps
    || !string.IsNullOrEmpty(endpoint.UserInfo)
    || !string.IsNullOrEmpty(endpoint.Query)
    || !string.IsNullOrEmpty(endpoint.Fragment)
    || !int.TryParse(args[2], out var timeoutSeconds)
    || timeoutSeconds is < 1 or > 30)
{
    Console.Error.WriteLine("Usage: <root-certificate> <https-url-without-credentials-query-or-fragment> <timeout-seconds>");
    return 64;
}

using var trustedRoot = X509CertificateLoader.LoadCertificate(File.ReadAllBytes(args[0]));
using var handler = new HttpClientHandler
{
    CheckCertificateRevocationList = false,
};
handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
{
    if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        return false;

    using var serverCertificate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
    using var chain = new X509Chain();
    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
    chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
    chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
    return chain.Build(serverCertificate);
};
using var client = new HttpClient(handler)
{
    Timeout = TimeSpan.FromSeconds(timeoutSeconds),
};
using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseHeadersRead);
Console.WriteLine((int)response.StatusCode);
return 0;
