using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kindlekeep_api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddJourneyMonitors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MonitorType",
                table: "MonitorTargets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "JourneySteps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    StepOrder = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "text", nullable: false),
                    UrlOrPath = table.Column<string>(type: "text", nullable: false),
                    Headers = table.Column<string>(type: "text", nullable: true),
                    Body = table.Column<string>(type: "text", nullable: true),
                    CaptureAs = table.Column<string>(type: "text", nullable: true),
                    CaptureJsonPath = table.Column<string>(type: "text", nullable: true),
                    AssertJsonPath = table.Column<string>(type: "text", nullable: true),
                    AssertEquals = table.Column<string>(type: "text", nullable: true),
                    ExpectedStatusCode = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JourneySteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JourneySteps_MonitorTargets_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "MonitorTargets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JourneySteps_MonitorId_StepOrder",
                table: "JourneySteps",
                columns: new[] { "MonitorId", "StepOrder" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JourneySteps");

            migrationBuilder.DropColumn(
                name: "MonitorType",
                table: "MonitorTargets");
        }
    }
}
