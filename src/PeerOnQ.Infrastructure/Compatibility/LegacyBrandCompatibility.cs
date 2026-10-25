namespace PeerOnQ.Infrastructure.Compatibility;

/// <summary>
/// Opaque identifiers needed only to read data written before the PeerOnQ migration.
/// Keep all additions evidence-backed and covered by migration tests.
/// </summary>
internal static class LegacyBrandCompatibility
{
    private const string PreviousBrand = "Link" + "ora";
    private const string PreviousBrandLower = "link" + "ora";

    public const string DataDirectoryName = PreviousBrand;
    public const string DatabaseFileName = PreviousBrandLower + ".db";
    public const string LogFilePattern = PreviousBrandLower + "-*.log";
    public const string LogFilePrefix = PreviousBrandLower + "-";
    public const string DeviceSecretEntropyV1 = PreviousBrand + ".DeviceSecret.v1";
    public const string DesktopProductId = "com." + PreviousBrandLower + ".desktop";
}
