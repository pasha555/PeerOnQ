namespace PeerOnQ.Cloud.Domain.Entities;

public sealed class Installation
{
    private Installation() { }

    private Installation(
        Guid id,
        byte[] claimedPublicDeviceIdHash,
        PlatformKind platform,
        ArchitectureKind architecture,
        string appVersion,
        string osVersion,
        InstallChannel installChannel,
        string protocolVersion,
        string region,
        DateTimeOffset now)
    {
        Id = id;
        ClaimedPublicDeviceIdHash = claimedPublicDeviceIdHash.ToArray();
        Platform = platform;
        Architecture = architecture;
        AppVersion = appVersion;
        OsVersion = osVersion;
        InstallChannel = installChannel;
        ProtocolVersion = protocolVersion;
        FirstSeenAtUtc = now;
        LastSeenAtUtc = now;
        CurrentRegion = region;
        CreatedAtUtc = now;
        ConcurrencyVersion = 1;
    }

    public Guid Id { get; private set; }
    public Guid? DeviceId { get; private set; }
    public byte[] ClaimedPublicDeviceIdHash { get; private set; } = [];
    public PlatformKind Platform { get; private set; }
    public ArchitectureKind Architecture { get; private set; }
    public string AppVersion { get; private set; } = string.Empty;
    public string OsVersion { get; private set; } = string.Empty;
    public InstallChannel InstallChannel { get; private set; }
    public string ProtocolVersion { get; private set; } = string.Empty;
    public DateTimeOffset FirstSeenAtUtc { get; private set; }
    public DateTimeOffset LastSeenAtUtc { get; private set; }
    public DateTimeOffset? LastOnlineAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public bool IsBlocked { get; private set; }
    public string? BlockReason { get; private set; }
    public string CurrentRegion { get; private set; } = string.Empty;
    public DateTimeOffset? UnregisteredAtUtc { get; private set; }
    public long ConcurrencyVersion { get; private set; }
    public int ProofBindingVersion { get; private set; }
    public DateTimeOffset? ProofBoundAtUtc { get; private set; }

    public static Installation Register(
        Guid id,
        byte[] claimedPublicDeviceIdHash,
        PlatformKind platform,
        ArchitectureKind architecture,
        string appVersion,
        string osVersion,
        InstallChannel installChannel,
        string protocolVersion,
        string region,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Installation ID is required.", nameof(id));
        if (claimedPublicDeviceIdHash is not { Length: 32 })
            throw new ArgumentException("The keyed public device ID hash must contain 32 bytes.", nameof(claimedPublicDeviceIdHash));

        return new Installation(
            id,
            claimedPublicDeviceIdHash,
            platform,
            architecture,
            Device.NormalizeRequired(appVersion, 64, nameof(appVersion)),
            Device.NormalizeRequired(osVersion, 128, nameof(osVersion)),
            installChannel,
            Device.NormalizeRequired(protocolVersion, 32, nameof(protocolVersion)),
            Device.NormalizeRequired(region, 64, nameof(region)),
            now);
    }

    public static Installation RegisterProofBound(
        Guid id,
        Guid deviceId,
        byte[] publicDeviceIdHash,
        PlatformKind platform,
        ArchitectureKind architecture,
        string appVersion,
        string osVersion,
        InstallChannel installChannel,
        string protocolVersion,
        string region,
        DateTimeOffset now)
    {
        var installation = Register(id, publicDeviceIdHash, platform, architecture, appVersion,
            osVersion, installChannel, protocolVersion, region, now);
        installation.DeviceId = deviceId == Guid.Empty
            ? throw new ArgumentException("Device ID is required.", nameof(deviceId))
            : deviceId;
        installation.ProofBindingVersion = 1;
        installation.ProofBoundAtUtc = now;
        return installation;
    }

    public void ConfirmProofBinding(
        Guid deviceId,
        byte[] publicDeviceIdHash,
        PlatformKind platform,
        ArchitectureKind architecture,
        string appVersion,
        string osVersion,
        InstallChannel installChannel,
        string protocolVersion,
        string region,
        DateTimeOffset now)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (publicDeviceIdHash is not { Length: 32 })
            throw new ArgumentException("The keyed public device ID hash must contain 32 bytes.", nameof(publicDeviceIdHash));
        if (DeviceId is { } existing && existing != deviceId)
            throw new InvalidOperationException("The installation is already proof-bound to another device identity.");
        if (ProofBindingVersion > 0 && (Platform != platform || Architecture != architecture))
            throw new InvalidOperationException("Proof-bound installation platform and architecture cannot change.");

        DeviceId = deviceId;
        ClaimedPublicDeviceIdHash = publicDeviceIdHash.ToArray();
        Platform = platform;
        Architecture = architecture;
        AppVersion = Device.NormalizeRequired(appVersion, 64, nameof(appVersion));
        OsVersion = Device.NormalizeRequired(osVersion, 128, nameof(osVersion));
        InstallChannel = installChannel;
        ProtocolVersion = Device.NormalizeRequired(protocolVersion, 32, nameof(protocolVersion));
        CurrentRegion = Device.NormalizeRequired(region, 64, nameof(region));
        ProofBindingVersion = 1;
        ProofBoundAtUtc ??= now;
        LastSeenAtUtc = now;
        UnregisteredAtUtc = null;
        ConcurrencyVersion++;
    }

    public bool Claims(byte[] publicDeviceIdHash) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ClaimedPublicDeviceIdHash, publicDeviceIdHash);

    public void AttachDevice(Guid deviceId, byte[] publicDeviceIdHash, DateTimeOffset now)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
        if (!Claims(publicDeviceIdHash)) throw new InvalidOperationException("Installation identity claim does not match the device.");
        if (DeviceId is { } existing && existing != deviceId)
            throw new InvalidOperationException("The installation is already attached to another device identity.");
        DeviceId = deviceId;
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    public void Heartbeat(string appVersion, string region, DateTimeOffset now)
    {
        EnsureUsable();
        AppVersion = Device.NormalizeRequired(appVersion, 64, nameof(appVersion));
        CurrentRegion = Device.NormalizeRequired(region, 64, nameof(region));
        LastSeenAtUtc = now;
        LastOnlineAtUtc = now;
        ConcurrencyVersion++;
    }

    public void UpdateVersion(string appVersion, string osVersion, ArchitectureKind architecture, InstallChannel channel, DateTimeOffset now)
    {
        EnsureUsable();
        if (Architecture != architecture)
            throw new InvalidOperationException("Installation architecture cannot change for an existing installation identity.");
        AppVersion = Device.NormalizeRequired(appVersion, 64, nameof(appVersion));
        OsVersion = Device.NormalizeRequired(osVersion, 128, nameof(osVersion));
        InstallChannel = channel;
        LastSeenAtUtc = now;
        ConcurrencyVersion++;
    }

    public void Block(string reason)
    {
        IsBlocked = true;
        BlockReason = Device.NormalizeRequired(reason, 256, nameof(reason));
        ConcurrencyVersion++;
    }

    public void Unblock()
    {
        IsBlocked = false;
        BlockReason = null;
        ConcurrencyVersion++;
    }

    public void Unregister(DateTimeOffset now)
    {
        if (UnregisteredAtUtc is null)
        {
            UnregisteredAtUtc = now;
            ConcurrencyVersion++;
        }
    }

    private void EnsureUsable()
    {
        if (IsBlocked) throw new InvalidOperationException("The installation is blocked.");
        if (UnregisteredAtUtc is not null) throw new InvalidOperationException("The installation is unregistered.");
    }
}
