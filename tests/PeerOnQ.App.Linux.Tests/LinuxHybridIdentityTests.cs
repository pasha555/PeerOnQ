using System.Security.Cryptography;
using PeerOnQ.Application.Security;
using Xunit;

namespace PeerOnQ.App.Linux.Tests;

[Collection("Linux desktop")]
public sealed class LinuxHybridIdentityTests
{
    [LinuxHybridFact]
    public async Task ActualKeyringBackedHybridIdentitySurvivesRecreationAndVerifiesBothTranscriptSignatures()
    {
        var previous = Environment.GetEnvironmentVariable("PEERONQ_DATA_DIR");
        Environment.SetEnvironmentVariable("PEERONQ_DATA_DIR", Path.Combine(Path.GetTempPath(),
            "peeronq-linux-hybrid-" + Guid.NewGuid().ToString("N")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? firstFingerprint = null;
        try
        {
            for (var lifecycle = 0; lifecycle < 2; lifecycle++)
            {
                await using var services = await LinuxAppServices.CreateAsync(deadline.Token);
                var identity = await services.HybridIdentity.GetPublicIdentityAsync(deadline.Token);
                var legacyFingerprint = Convert.ToHexString(SHA256.HashData(
                    Convert.FromBase64String(services.Identity.PublicKey!))).ToLowerInvariant();
                var transcript = SHA512.HashData("peeronq-linux-keyring-hybrid-acceptance-v1"u8);
                var context = SecureSessionProtocol.ClientKeySignatureContext;
                var signature = await services.HybridIdentity.SignTranscriptAsync(transcript, context, deadline.Token);
                Assert.Equal(identity.Fingerprint, HybridIdentityVerifier.VerifyAndFingerprint(
                    identity, legacyFingerprint, transcript, context, signature));
                if (firstFingerprint is not null) Assert.Equal(firstFingerprint, identity.Fingerprint);
                firstFingerprint = identity.Fingerprint;

                var modifiedTranscript = transcript.ToArray();
                modifiedTranscript[0] ^= 1;
                Assert.Throws<CryptographicException>(() => HybridIdentityVerifier.VerifyAndFingerprint(
                    identity, legacyFingerprint, modifiedTranscript, context, signature));
                var modifiedPostQuantumSignature = signature.MLDsa65Signature.ToArray();
                modifiedPostQuantumSignature[^1] ^= 1;
                Assert.Throws<CryptographicException>(() => HybridIdentityVerifier.VerifyAndFingerprint(
                    identity, legacyFingerprint, transcript, context,
                    signature with { MLDsa65Signature = modifiedPostQuantumSignature }));
            }
        }
        finally { Environment.SetEnvironmentVariable("PEERONQ_DATA_DIR", previous); }
    }

    private sealed class LinuxHybridFactAttribute : FactAttribute
    {
        public LinuxHybridFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("PEERONQ_LINUX_NATIVE_TEST") != "1")
                Skip = "Requires a real Linux Secret Service; enable PEERONQ_LINUX_NATIVE_TEST=1.";
        }
    }
}
