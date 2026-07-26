using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kindlekeep_api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIncidentEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EscalationDelayMinutes",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<DateTime>(
                name: "AcknowledgedAt",
                table: "AlertIncidents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EscalatedAt",
                table: "AlertIncidents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EscalationDelayMinutes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                table: "AlertIncidents");

            migrationBuilder.DropColumn(
                name: "EscalatedAt",
                table: "AlertIncidents");
        }
    }
}
