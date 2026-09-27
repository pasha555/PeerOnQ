using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace PeerOnQ.Cloud.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedAdminRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AdminRoles",
                columns: new[] { "Id", "Name", "RequiresMfa" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), "Owner", true },
                    { new Guid("10000000-0000-0000-0000-000000000002"), "SecurityAdministrator", true },
                    { new Guid("10000000-0000-0000-0000-000000000003"), "OperationsAdministrator", true },
                    { new Guid("10000000-0000-0000-0000-000000000004"), "SupportAgent", true },
                    { new Guid("10000000-0000-0000-0000-000000000005"), "ReleaseManager", true },
                    { new Guid("10000000-0000-0000-0000-000000000006"), "ReadOnlyAnalyst", false }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException(
                "Admin role seeding is forward-only. Apply a reviewed compensating migration instead of deleting authorization roles.");
        }
    }
}
