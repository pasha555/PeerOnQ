using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CloudDbContext))]
[Migration("20261018193000_AddOrganizationSystemAudioPolicy")]
public sealed class AddOrganizationSystemAudioPolicy : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.AddColumn<bool>(
        name: "SystemAudioAllowed",
        table: "OrganizationPolicies",
        type: "boolean",
        nullable: false,
        defaultValue: false);

    // Preserve stored consent on rollback. Older binaries ignore this additive column.
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException(
        "Use the compatible previous application version while retaining the additive system-audio policy column.");
}
