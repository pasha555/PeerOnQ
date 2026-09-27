namespace PeerOnQ.Domain.Identity;

/// <summary>
/// Stable identity of one PeerOnQ installation.
/// The private key and device secret are never part of this record; they live in
/// protected OS storage and are referenced by <see cref="InternalId"/>.
/// </summary>
public sealed record DeviceIdentity
{
    public const int CurrentIdentityVersion = 1;

    public required Guid InternalId { get; init; }
    public required PeerOnQId PublicId { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int IdentityVersion { get; init; } = CurrentIdentityVersion;
    public bool PublicIdServerAssigned { get; init; }

    /// <summary>Base64 public key, when the platform provided a key pair. Never the private half.</summary>
    public string? PublicKey { get; init; }

    public static DeviceIdentity Create(string displayName, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("Display name is required.", nameof(displayName));
        }

        return new DeviceIdentity
        {
            InternalId = Guid.NewGuid(),
            PublicId = PeerOnQId.NewId(),
            DisplayName = displayName.Trim(),
            CreatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            IdentityVersion = CurrentIdentityVersion,
        };
    }

    /// <summary>Log-safe projection. Used by every logger in the product.</summary>
    public string ToLogString() => $"{DisplayName} ({PublicId.Masked})";
}
