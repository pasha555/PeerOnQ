using System.Text;
using PeerOnQ.Application.Collaboration;
using PeerOnQ.Domain.Collaboration;
using PeerOnQ.Domain.Identity;
using PeerOnQ.Domain.Sessions;
using PeerOnQ.Infrastructure.Persistence;
using PeerOnQ.Infrastructure.Security;
using Xunit;

namespace PeerOnQ.Infrastructure.Tests;

public sealed class Phase4PersistenceTests
{
    [Fact]
    public async Task Address_book_trust_and_unattended_profile_is_DPAPI_protected_at_rest()
    {
        using var environment = new TempEnvironment();
        var protectedStore = new ProtectedCollaborationProfileStore(
            new DpapiSecretStore(environment.Paths.SecretsDirectory));
        var marker = "private-note-that-must-not-be-plaintext";
        await protectedStore.SaveAsync(new CollaborationProfileSnapshot
        {
            AddressBookDevices =
            [
                new AddressBookDevice
                {
                    RecordId = Guid.NewGuid(),
                    DeviceId = PeerOnQId.Parse("LNK-483-921-756-204"),
                    DisplayName = "Office",
                    OperatingSystem = "Windows",
                    Notes = marker,
                },
            ],
            UnattendedAccess = new UnattendedAccessSettings
            {
                Enabled = true,
                Password = ProtectedCollaborationProfileStore.DerivePassword("Correct-Horse-9!Battery"),
            },
        });

        var raw = await File.ReadAllBytesAsync(Directory.EnumerateFiles(environment.Paths.SecretsDirectory, "*.dpapi").Single());
        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        var storedDevice = Assert.Single((await protectedStore.LoadAsync()).AddressBookDevices);
        Assert.Equal(marker, storedDevice.Notes);
        Assert.Equal("483-921-756-204", storedDevice.DeviceId.Display);
        Assert.Equal("Windows", storedDevice.OperatingSystem);
    }

    [Fact]
    public async Task Existing_object_shaped_device_ids_are_migrated_when_the_profile_is_loaded()
    {
        using var environment = new TempEnvironment();
        var secrets = new DpapiSecretStore(environment.Paths.SecretsDirectory);
        var recordId = Guid.NewGuid();
        var legacyJson = $$"""
            {
              "version": 1,
              "addressBookDevices": [
                {
                  "recordId": "{{recordId}}",
                  "deviceId": { "value": "LNK-321-654-987-012", "display": "321-654-987-012" },
                  "displayName": "Legacy device",
                  "operatingSystem": "User supplied legacy OS",
                  "lastSeenAt": "2025-01-01T00:00:00Z",
                  "tags": [],
                  "groupIds": []
                }
              ]
            }
            """;
        await secrets.SetAsync("collaboration-profile-v1", Encoding.UTF8.GetBytes(legacyJson));

        var loaded = await new ProtectedCollaborationProfileStore(secrets).LoadAsync();

        var device = Assert.Single(loaded.AddressBookDevices);
        Assert.Equal(recordId, device.RecordId);
        Assert.Equal("321-654-987-012", device.DeviceId.Display);
        Assert.Null(device.OperatingSystemVerifiedAt);
        Assert.Null(device.LastSeenVerifiedAt);

        var addressBook = new AddressBookService(new ProtectedCollaborationProfileStore(secrets));
        var visible = Assert.Single(await addressBook.SearchAsync()).Device;
        Assert.Null(visible.OperatingSystem);
        Assert.Null(visible.LastSeenAt);
    }

    [Fact]
    public async Task Security_audit_persists_safe_events_and_removes_sensitive_metadata_keys()
    {
        using var environment = new TempEnvironment();
        var audit = new SqliteSecurityAuditLog(environment.Database);
        await audit.AppendAsync(new SecurityAuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = SecurityAuditEventType.FileTransferCompleted,
            OccurredAt = DateTimeOffset.UtcNow,
            Outcome = "completed",
            SafeMetadata = new Dictionary<string, string>
            {
                ["transferId"] = Guid.NewGuid().ToString("N"),
                ["filename"] = "secret-budget.xlsx",
                ["clipboardContent"] = "never-store-this",
            },
        });

        var stored = Assert.Single(await audit.ReadRecentAsync(10));
        Assert.Equal("completed", stored.Outcome);
        Assert.Contains("transferId", stored.SafeMetadata.Keys);
        Assert.DoesNotContain(stored.SafeMetadata.Keys, key => key.Contains("file", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(stored.SafeMetadata.Values, value => value.Contains("never-store-this", StringComparison.Ordinal));
    }

    [Fact]
    public void Database_migrates_to_phase4_schema_version()
    {
        using var environment = new TempEnvironment();
        using var connection = environment.Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info;";
        Assert.Equal(PeerOnQDatabase.SchemaVersion, Convert.ToInt32(command.ExecuteScalar()));
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='security_audit';";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
    }
}
