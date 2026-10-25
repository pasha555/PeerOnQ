using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerIdentityOrganizationsAndPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    MfaEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MfaSecretCiphertext = table.Column<byte[]>(type: "bytea", maxLength: 4096, nullable: true),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntilUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLoginAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletionRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AccountDataRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountDataRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountDataRequests_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerAccountTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerAccountTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerAccountTokens_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRecoveryCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRecoveryCodes_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CustomerSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
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
                    table.PrimaryKey("PK_CustomerSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerSessions_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CustomerSessions_CustomerSessions_ReplacedBySessionId",
                        column: x => x.ReplacedBySessionId,
                        principalTable: "CustomerSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerTrustedDevices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceKeyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerTrustedDevices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerTrustedDevices_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OwnerAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletionRequestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Organizations_CustomerAccounts_OwnerAccountId",
                        column: x => x.OwnerAccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerSecurityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Result = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TimestampUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SafeMetadata = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerSecurityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerSecurityEvents_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerSecurityEvents_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TokenHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    InvitedByAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrganizationInvitations_CustomerAccounts_InvitedByAccountId",
                        column: x => x.InvitedByAccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OrganizationInvitations_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationMemberships",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationMemberships", x => new { x.OrganizationId, x.AccountId });
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizationMemberships_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationPolicies",
                columns: table => new
                {
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ViewOnlyAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    FullControlAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    FileTransferAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    ClipboardAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    UnattendedAccessAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    MfaRequired = table.Column<bool>(type: "boolean", nullable: false),
                    HybridSecurityRequired = table.Column<bool>(type: "boolean", nullable: false),
                    TrustedDeviceLifetimeDays = table.Column<int>(type: "integer", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    ApprovedRelayRegionsCsv = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MinimumClientVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationPolicies", x => x.OrganizationId);
                    table.ForeignKey(
                        name: "FK_OrganizationPolicies_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Teams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyVersion = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Teams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Teams_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TeamMemberships",
                columns: table => new
                {
                    TeamId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    JoinedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TeamMemberships", x => new { x.TeamId, x.AccountId });
                    table.ForeignKey(
                        name: "FK_TeamMemberships_CustomerAccounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "CustomerAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TeamMemberships_Teams_TeamId",
                        column: x => x.TeamId,
                        principalTable: "Teams",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountDataRequests_AccountId_RequestedAtUtc",
                table: "AccountDataRequests",
                columns: new[] { "AccountId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountTokens_AccountId_Purpose_ExpiresAtUtc",
                table: "CustomerAccountTokens",
                columns: new[] { "AccountId", "Purpose", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccountTokens_TokenHash",
                table: "CustomerAccountTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccounts_Email",
                table: "CustomerAccounts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerAccounts_Status",
                table: "CustomerAccounts",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRecoveryCodes_AccountId_CodeHash",
                table: "CustomerRecoveryCodes",
                columns: new[] { "AccountId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityEvents_AccountId_TimestampUtc",
                table: "CustomerSecurityEvents",
                columns: new[] { "AccountId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSecurityEvents_OrganizationId_TimestampUtc",
                table: "CustomerSecurityEvents",
                columns: new[] { "OrganizationId", "TimestampUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSessions_AccountId_ExpiresAtUtc",
                table: "CustomerSessions",
                columns: new[] { "AccountId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSessions_FamilyId",
                table: "CustomerSessions",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSessions_RefreshTokenHash",
                table: "CustomerSessions",
                column: "RefreshTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerSessions_ReplacedBySessionId",
                table: "CustomerSessions",
                column: "ReplacedBySessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTrustedDevices_AccountId_ExpiresAtUtc",
                table: "CustomerTrustedDevices",
                columns: new[] { "AccountId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTrustedDevices_DeviceKeyHash",
                table: "CustomerTrustedDevices",
                column: "DeviceKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_InvitedByAccountId",
                table: "OrganizationInvitations",
                column: "InvitedByAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_OrganizationId_Email_ExpiresAtUtc",
                table: "OrganizationInvitations",
                columns: new[] { "OrganizationId", "Email", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationInvitations_TokenHash",
                table: "OrganizationInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationMemberships_AccountId_RevokedAtUtc",
                table: "OrganizationMemberships",
                columns: new[] { "AccountId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_OwnerAccountId",
                table: "Organizations",
                column: "OwnerAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TeamMemberships_AccountId",
                table: "TeamMemberships",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Teams_OrganizationId_Name",
                table: "Teams",
                columns: new[] { "OrganizationId", "Name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_CustomerAccounts_OwnerUserId",
                table: "Devices",
                column: "OwnerUserId",
                principalTable: "CustomerAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Devices_Organizations_OwnerOrganizationId",
                table: "Devices",
                column: "OwnerOrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_CustomerSecurityEvents_AppendOnly"
                BEFORE UPDATE OR DELETE ON "CustomerSecurityEvents"
                FOR EACH ROW EXECUTE FUNCTION peeronq_reject_append_only_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"TR_CustomerSecurityEvents_AppendOnly\" ON \"CustomerSecurityEvents\";");

            migrationBuilder.DropForeignKey(
                name: "FK_Devices_CustomerAccounts_OwnerUserId",
                table: "Devices");

            migrationBuilder.DropForeignKey(
                name: "FK_Devices_Organizations_OwnerOrganizationId",
                table: "Devices");

            migrationBuilder.DropTable(
                name: "AccountDataRequests");

            migrationBuilder.DropTable(
                name: "CustomerAccountTokens");

            migrationBuilder.DropTable(
                name: "CustomerRecoveryCodes");

            migrationBuilder.DropTable(
                name: "CustomerSecurityEvents");

            migrationBuilder.DropTable(
                name: "CustomerSessions");

            migrationBuilder.DropTable(
                name: "CustomerTrustedDevices");

            migrationBuilder.DropTable(
                name: "OrganizationInvitations");

            migrationBuilder.DropTable(
                name: "OrganizationMemberships");

            migrationBuilder.DropTable(
                name: "OrganizationPolicies");

            migrationBuilder.DropTable(
                name: "TeamMemberships");

            migrationBuilder.DropTable(
                name: "Teams");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropTable(
                name: "CustomerAccounts");
        }
    }
}
