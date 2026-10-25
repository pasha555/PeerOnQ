using System.Security.Cryptography;
using System.Text.Json;

return await WebsiteReleaseSigner.RunAsync(args);

internal static class WebsiteReleaseSigner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Parse(args);
            var versionText = Required(options, "version");
            if (!Version.TryParse(versionText, out var version) || version.Build < 0 || versionText.Any(value => !char.IsAsciiDigit(value) && value != '.'))
                throw new InvalidOperationException("The website version must contain at least three numeric parts.");
            var archivePath = Path.GetFullPath(Required(options, "archive"));
            var privateKeyPath = Path.GetFullPath(Required(options, "private-key"));
            var outputPath = Path.GetFullPath(Required(options, "output"));
            var keyId = Required(options, "key-id");
            if (keyId.Length is < 3 or > 128) throw new InvalidOperationException("The key ID is invalid.");
            if (!File.Exists(archivePath) || !File.Exists(privateKeyPath)) throw new FileNotFoundException("A required signing input is missing.");
            if (File.Exists(outputPath)) throw new InvalidOperationException("Refusing to overwrite an existing signed manifest.");
            var archiveName = Path.GetFileName(archivePath);
            if (archiveName != $"PeerOnQ-website-{versionText}.zip") throw new InvalidOperationException("The archive name does not match the website version.");
            var archiveLength = new FileInfo(archivePath).Length;
            if (archiveLength is <= 0 or > 96L * 1024 * 1024) throw new InvalidOperationException("The website archive size is invalid.");

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(await File.ReadAllTextAsync(privateKeyPath));
            if (ecdsa.KeySize != 256) throw new InvalidOperationException("The website signing key must be ECDSA P-256.");
            var expectedPublicKey = Convert.FromBase64String(Required(options, "expected-public-key"));
            var actualPublicKey = ecdsa.ExportSubjectPublicKeyInfo();
            if (!CryptographicOperations.FixedTimeEquals(expectedPublicKey, actualPublicKey))
                throw new InvalidOperationException("The private website key does not match the server trust root.");

            var issuedAt = DateTimeOffset.UtcNow;
            await using var archiveStream = File.OpenRead(archivePath);
            var archiveSha256 = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream)).ToLowerInvariant();
            var payload = JsonSerializer.SerializeToUtf8Bytes(new SignedPayload(
                1,
                "com.peeronq.website",
                versionText,
                issuedAt,
                issuedAt.AddDays(7),
                archiveName,
                archiveSha256,
                archiveLength,
                "index.html"), JsonOptions);
            var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            var envelope = JsonSerializer.SerializeToUtf8Bytes(new SignedEnvelope(
                1,
                keyId,
                Convert.ToBase64String(payload),
                Convert.ToBase64String(signature)), JsonOptions);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllBytesAsync(outputPath, envelope.Concat([(byte)'\n']).ToArray());
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or CryptographicException or InvalidOperationException)
        {
            Console.Error.WriteLine($"PeerOnQ website signer: {exception.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("Every signing option requires exactly one value.");
            var key = args[index][2..];
            if (key is not ("version" or "archive" or "private-key" or "expected-public-key" or "key-id" or "output") || !values.TryAdd(key, args[index + 1]))
                throw new ArgumentException("An unknown or duplicate signing option was provided.");
        }
        return values;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"The --{key} option is required.");

    private sealed record SignedPayload(
        int SchemaVersion,
        string ProductId,
        string Version,
        DateTimeOffset IssuedAt,
        DateTimeOffset ExpiresAt,
        string ArchiveFileName,
        string ArchiveSha256,
        long ArchiveSizeBytes,
        string EntryPoint);

    private sealed record SignedEnvelope(int SchemaVersion, string KeyId, string Payload, string Signature);
}
