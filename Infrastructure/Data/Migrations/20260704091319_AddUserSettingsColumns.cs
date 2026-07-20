using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kindlekeep_api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserSettingsColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscordWebhookUrl",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableEmailNotifications",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscordWebhookUrl",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "EnableEmailNotifications",
                table: "Users");
        }
    }
}
