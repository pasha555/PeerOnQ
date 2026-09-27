using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionActivityHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastActivityAtUtc",
                table: "RemoteSessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LastActivityEventId",
                table: "RemoteSessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "RemoteSessions"
                SET "LastActivityAtUtc" = COALESCE("ConnectedAtUtc", "StartedAtUtc")
                WHERE "LastActivityAtUtc" IS NULL;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "LastActivityAtUtc",
                table: "RemoteSessions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RemoteSessions_Lifecycle_LastActivityAtUtc",
                table: "RemoteSessions",
                columns: new[] { "Lifecycle", "LastActivityAtUtc" },
                filter: "\"EndedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RemoteSessions_Lifecycle_LastActivityAtUtc",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "LastActivityAtUtc",
                table: "RemoteSessions");

            migrationBuilder.DropColumn(
                name: "LastActivityEventId",
                table: "RemoteSessions");
        }
    }
}
