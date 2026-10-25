using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PeerOnQ.Application.Abstractions;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;

namespace PeerOnQ.Application.Collaboration;

public sealed record PasswordCredential
{
    public required byte[] Salt { get; init; }
    public required byte[] Hash { get; init; }
    public int Iterations { get; init; } = 600_000;
}

public sealed record UnattendedAccessSettings
{
    public bool Enabled { get; init; }
    public bool DeviceAuthenticationEnabled { get; init; }
    public PeerOnQ.Domain.Sessions.SessionPermission AllowedPermissions { get; init; } =
        PeerOnQ.Domain.Sessions.SessionPermission.ViewScreen;
    public PasswordCredential? Password { get; init; }
    public IReadOnlyList<byte[]> RecoveryCodeHashes { get; init; } = [];
    public DateTimeOffset? EnabledAt { get; init; }
    public DateTimeOffset? CredentialsRotatedAt { get; init; }
    public int FailedAttempts { get; init; }
    public DateTimeOffset? LockedUntil { get; init; }
}

public sealed record CollaborationProfileSnapshot
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<TrustedDevice> TrustedDevices { get; init; } = [];
    public IReadOnlyList<AddressBookDevice> AddressBookDevices { get; init; } = [];
    public IReadOnlyList<AddressBookGroup> AddressBookGroups { get; init; } = [];
    public UnattendedAccessSettings UnattendedAccess { get; init; } = new();
}

public interface ICollaborationProfileStore
{
    Task<CollaborationProfileSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CollaborationProfileSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>
/// Stores the complete Phase 4 profile as an OS-protected secret. In production the supplied
/// IDeviceSecretStore is DPAPI CurrentUser, so trusted fingerprints, notes, password hashes and
/// recovery hashes are never present in plaintext at rest.
/// </summary>
public sealed class ProtectedCollaborationProfileStore(IDeviceSecretStore secretStore) : ICollaborationProfileStore
{
    private const string SecretName = "collaboration-profile-v1";
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new PeerOnQIdJsonConverter());
        return options;
    }

    public async Task<CollaborationProfileSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var bytes = await secretStore.TryGetAsync(SecretName, cancellationToken);
        if (bytes is null) return new CollaborationProfileSnapshot();
        try
        {
            var result = JsonSerializer.Deserialize<CollaborationProfileSnapshot>(bytes, JsonOptions)
                         ?? throw new InvalidDataException("The collaboration profile is empty.");
            if (result.Version != 1) throw new InvalidDataException("Unsupported collaboration profile version.");
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task SaveAsync(CollaborationProfileSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Version != 1) throw new InvalidDataException("Unsupported collaboration profile version.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        try { await secretStore.SetAsync(SecretName, bytes, cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static PasswordCredential DerivePassword(string password, int iterations = 600_000)
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            return new PasswordCredential
            {
                Salt = salt,
                Hash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA512, 64),
                Iterations = iterations,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    public static bool VerifyPassword(string password, PasswordCredential credential)
    {
        if (credential.Iterations < 600_000 || credential.Salt.Length < 16 || credential.Hash.Length != 64)
            return false;
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            var candidate = Rfc2898DeriveBytes.Pbkdf2(
                passwordBytes, credential.Salt, credential.Iterations, HashAlgorithmName.SHA512, credential.Hash.Length);
            try { return CryptographicOperations.FixedTimeEquals(candidate, credential.Hash); }
            finally { CryptographicOperations.ZeroMemory(candidate); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private sealed class PeerOnQIdJsonConverter : JsonConverter<PeerOnQId>
    {
        public override PeerOnQId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && PeerOnQId.TryParse(reader.GetString(), out var stringId))
                return stringId;

            if (reader.TokenType == JsonTokenType.StartObject)
            {
                using var document = JsonDocument.ParseValue(ref reader);
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if ((property.Name.Equals("value", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("display", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String
                        && PeerOnQId.TryParse(property.Value.GetString(), out var legacyId))
                        return legacyId;
                }
            }

            throw new JsonException("The stored PeerOnQ ID is invalid.");
        }

        public override void Write(Utf8JsonWriter writer, PeerOnQId value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Display);
    }
}
