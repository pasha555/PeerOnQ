using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCloudPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequiresMfa = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    MfaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MfaSecretCiphertext = table.Column<byte[]>(type: "bytea", maxLength: 4096, nullable: true),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLoginAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsDisabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    RunbookUrl = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublishedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MinimumSupportedVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SecurityFloorVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RolloutPercentage = table.Column<int>(type: "integer", nullable: false),
                    SignedManifestDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactUri = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppReleases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IpRiskMetadata = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    UserAgentSummary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PublicDeviceIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    MaskedPublicDeviceId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdentityFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdentityVersion = table.Column<int>(type: "integer", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerOrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DownloadEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKeyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UniquenessKeyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Channel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Campaign = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    UserAgentFamily = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DownloadEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InfrastructureRegions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InfrastructureRegions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdminRecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminRecoveryCodes_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdminSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefreshTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedBySessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserAgentSummary = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AdminSessions_AdminSessions_ReplacedBySessionId",
                        column: x => x.ReplacedBySessionId,
                        principalTable: "AdminSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdminSessions_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AdminUserRoles",
                columns: table => new
                {
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminRoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    GrantedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminUserRoles", x => new { x.AdminUserId, x.AdminRoleId });
                    table.ForeignKey(
                        name: "FK_AdminUserRoles_AdminRoles_AdminRoleId",
                        column: x => x.AdminRoleId,
                        principalTable: "AdminRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AdminUserRoles_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AdminUserRoles_AdminUsers_GrantedByUserId",
                        column: x => x.GrantedByUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Installations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedPublicDeviceIdHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OsVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    InstallChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProtocolVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FirstSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastOnlineAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsBlocked = table.Column<bool>(type: "boolean", nullable: false),
                    BlockReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CurrentRegion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UnregisteredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Installations_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RemoteSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewerDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    HostDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConnectionPath = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ServerRegion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientVersionViewer = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClientVersionHost = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConnectedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureStage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UsedTurn = table.Column<bool>(type: "boolean", nullable: false),
                    ReconnectCount = table.Column<int>(type: "integer", nullable: false),
                    Lifecycle = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RemoteSessions_Devices_HostDeviceId",
                        column: x => x.HostDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RemoteSessions_Devices_ViewerDeviceId",
                        column: x => x.ViewerDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ServiceHealthSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Service = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LatencyMilliseconds = table.Column<double>(type: "double precision", nullable: false),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServiceHealthSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ServiceHealthSnapshots_InfrastructureRegions_RegionId",
                        column: x => x.RegionId,
                        principalTable: "InfrastructureRegions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DevicePresenceHistory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IntervalStartedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IntervalEndedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePresenceHistory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevicePresenceHistory_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticBundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsentGranted = table.Column<bool>(type: "boolean", nullable: false),
                    ConsentGrantedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadTokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    UploadTokenExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AppVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OsVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Architecture = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ErrorId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IssueCategory = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DatabaseSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    StorageObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SanitizedArchiveSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    ArchiveSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReferenceCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticBundles_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UpdateEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpdateEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UpdateEvents_AppReleases_ReleaseId",
                        column: x => x.ReleaseId,
                        principalTable: "AppReleases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UpdateEvents_Installations_InstallationId",
                        column: x => x.InstallationId,
                        principalTable: "Installations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SessionFailures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionFailures", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionFailures_RemoteSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "RemoteSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiagnosticAccessEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DiagnosticId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdminUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiagnosticAccessEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiagnosticAccessEvents_AdminUsers_AdminUserId",
                        column: x => x.AdminUserId,
                        principalTable: "AdminUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DiagnosticAccessEvents_DiagnosticBundles_DiagnosticId",
                        column: x => x.DiagnosticId,
                        principalTable: "DiagnosticBundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdminRecoveryCodes_AdminUserId_CodeHash",
                table: "AdminRecoveryCodes",
                columns: new[] { "AdminUserId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminRoles_Name",
                table: "AdminRoles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_AdminUserId_ExpiresAtUtc",
                table: "AdminSessions",
                columns: new[] { "AdminUserId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_RefreshTokenHash",
                table: "AdminSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminSessions_ReplacedBySessionId",
                table: "AdminSessions",
                column: "ReplacedBySessionId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserRoles_AdminRoleId",
                table: "AdminUserRoles",
                column: "AdminRoleId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUserRoles_GrantedByUserId",
                table: "AdminUserRoles",
                column: "GrantedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AdminUsers_Email",
                table: "AdminUsers",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_ResolvedAtUtc",
                table: "AlertEvents",
                column: "ResolvedAtUtc",
                filter: "\"ResolvedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_StartedAtUtc_Severity",
                table: "AlertEvents",
                columns: new[] { "StartedAtUtc", "Severity" });

            migrationBuilder.CreateIndex(
                name: "IX_AppReleases_Channel_Architecture_IsActive",
                table: "AppReleases",
                columns: new[] { "Channel", "Architecture", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_AppReleases_Version_Channel_Architecture",
                table: "AppReleases",
                columns: new[] { "Version", "Channel", "Architecture" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_Action_TimestampUtc",
                table: "AuditEvents",
                columns: new[] { "Action", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_CorrelationId",
                table: "AuditEvents",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_TimestampUtc",
                table: "AuditEvents",
                column: "TimestampUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DevicePresenceHistory_InstallationId_IntervalStartedAtUtc",
                table: "DevicePresenceHistory",
                columns: new[] { "InstallationId", "IntervalStartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DevicePresenceHistory_IntervalEndedAtUtc",
                table: "DevicePresenceHistory",
                column: "IntervalEndedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_IdentityFingerprint",
                table: "Devices",
                column: "IdentityFingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_LastSeenAtUtc",
                table: "Devices",
                column: "LastSeenAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OwnerOrganizationId",
                table: "Devices",
                column: "OwnerOrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_OwnerUserId",
                table: "Devices",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_PublicDeviceIdHash",
                table: "Devices",
                column: "PublicDeviceIdHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticAccessEvents_AdminUserId",
                table: "DiagnosticAccessEvents",
                column: "AdminUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticAccessEvents_DiagnosticId_OccurredAtUtc",
                table: "DiagnosticAccessEvents",
                columns: new[] { "DiagnosticId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticBundles_ExpiresAtUtc",
                table: "DiagnosticBundles",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticBundles_InstallationId",
                table: "DiagnosticBundles",
                column: "InstallationId");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticBundles_ReferenceCode",
                table: "DiagnosticBundles",
                column: "ReferenceCode",
                unique: true,
                filter: "\"ReferenceCode\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DiagnosticBundles_Status_CreatedAtUtc",
                table: "DiagnosticBundles",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_CompletedAtUtc",
                table: "DownloadEvents",
                column: "CompletedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_IdempotencyKeyHash",
                table: "DownloadEvents",
                column: "IdempotencyKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_StartedAtUtc_Version",
                table: "DownloadEvents",
                columns: new[] { "StartedAtUtc", "Version" });

            migrationBuilder.CreateIndex(
                name: "IX_DownloadEvents_UniquenessKeyHash_StartedAtUtc",
                table: "DownloadEvents",
                columns: new[] { "UniquenessKeyHash", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_InfrastructureRegions_Code",
                table: "InfrastructureRegions",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Installations_AppVersion",
                table: "Installations",
                column: "AppVersion");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_ClaimedPublicDeviceIdHash",
                table: "Installations",
                column: "ClaimedPublicDeviceIdHash");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_CurrentRegion_IsBlocked",
                table: "Installations",
                columns: new[] { "CurrentRegion", "IsBlocked" });

            migrationBuilder.CreateIndex(
                name: "IX_Installations_DeviceId",
                table: "Installations",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Installations_LastOnlineAtUtc",
                table: "Installations",
                column: "LastOnlineAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_EndedAtUtc",
                table: "RemoteSessions",
                column: "EndedAtUtc",
                filter: "\"EndedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_HostDeviceId",
                table: "RemoteSessions",
                column: "HostDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_ServerRegion_UsedTurn_StartedAtUtc",
                table: "RemoteSessions",
                columns: new[] { "ServerRegion", "UsedTurn", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_StartedAtUtc",
                table: "RemoteSessions",
                column: "StartedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_ViewerDeviceId",
                table: "RemoteSessions",
                column: "ViewerDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthSnapshots_ObservedAtUtc",
                table: "ServiceHealthSnapshots",
                column: "ObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ServiceHealthSnapshots_RegionId_Service_ObservedAtUtc",
                table: "ServiceHealthSnapshots",
                columns: new[] { "RegionId", "Service", "ObservedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionFailures_OccurredAtUtc_Code",
                table: "SessionFailures",
                columns: new[] { "OccurredAtUtc", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionFailures_SessionId",
                table: "SessionFailures",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_UpdateEvents_InstallationId_OccurredAtUtc",
                table: "UpdateEvents",
                columns: new[] { "InstallationId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UpdateEvents_ReleaseId_Kind_OccurredAtUtc",
                table: "UpdateEvents",
                columns: new[] { "ReleaseId", "Kind", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "The initial cloud-platform migration is forward-only. Restore a verified PostgreSQL backup instead of destructively dropping production telemetry and audit tables.");
        }
    }
}
