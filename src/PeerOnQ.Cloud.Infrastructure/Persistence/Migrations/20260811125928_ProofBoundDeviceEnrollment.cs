using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProofBoundDeviceEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProofBindingVersion",
                table: "Installations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProofBoundAtUtc",
                table: "Installations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PublicDeviceIdCollisionCounter",
                table: "Devices",
                type: "integer",
                nullable: false,
                defaultValue: -1);

            migrationBuilder.AddColumn<int>(
                name: "PublicDeviceIdKeyVersion",
                table: "Devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Devices_ServerAliasBinding",
                table: "Devices",
                sql: "(\"PublicDeviceIdCollisionCounter\" = -1 AND \"PublicDeviceIdKeyVersion\" = 0) OR (\"PublicDeviceIdCollisionCounter\" BETWEEN 0 AND 4095 AND \"PublicDeviceIdKeyVersion\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Installations_ProofBinding",
                table: "Installations",
                sql: "(\"ProofBindingVersion\" = 0 AND \"ProofBoundAtUtc\" IS NULL) OR (\"ProofBindingVersion\" = 1 AND \"ProofBoundAtUtc\" IS NOT NULL AND \"DeviceId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("PeerOnQ cloud migrations are forward-only. Restore a verified backup instead.");
        }
    }
}
