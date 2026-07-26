using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kindlekeep_api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCopilotCacheToSecurityAudits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CopilotExplanation",
                table: "SecurityAudits",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CopilotGeneratedAt",
                table: "SecurityAudits",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CopilotExplanation",
                table: "SecurityAudits");

            migrationBuilder.DropColumn(
                name: "CopilotGeneratedAt",
                table: "SecurityAudits");
        }
    }
}
