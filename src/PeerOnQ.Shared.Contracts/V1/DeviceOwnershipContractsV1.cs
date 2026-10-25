namespace PeerOnQ.Shared.Contracts.V1;

public sealed record CreateDeviceOwnershipLinkResponse(string Code, DateTimeOffset ExpiresAtUtc);
public sealed record DeviceOwnershipLinkStatusResponse(bool Linked);
