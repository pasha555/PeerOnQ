using System.Globalization;
using System.Security.Cryptography;

namespace PeerOnQ.Admin.Api;

public static class TotpVerifier
{
    private const int Digits = 6;
    private const int StepSeconds = 30;

    public static bool Verify(ReadOnlySpan<byte> secret, string code, DateTimeOffset now)
    {
        if (secret.Length < 16 || code.Length != Digits || code.Any(character => !char.IsAsciiDigit(character))) return false;
        var supplied = int.Parse(code, NumberStyles.None, CultureInfo.InvariantCulture);
        var currentCounter = now.ToUnixTimeSeconds() / StepSeconds;
        var valid = 0;
        for (var offset = -1; offset <= 1; offset++)
        {
            var expected = Generate(secret, currentCounter + offset);
            valid |= CryptographicOperations.FixedTimeEquals(
                BitConverter.GetBytes(expected),
                BitConverter.GetBytes(supplied)) ? 1 : 0;
        }
        return valid == 1;
    }

    private static int Generate(ReadOnlySpan<byte> secret, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        using var hmac = new HMACSHA1(secret.ToArray());
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        return binary % 1_000_000;
    }
}
