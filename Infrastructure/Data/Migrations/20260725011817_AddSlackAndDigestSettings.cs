using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace kindlekeep_api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSlackAndDigestSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DigestEnabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SlackWebhookUrl",
                table: "Users",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DigestEnabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "SlackWebhookUrl",
                table: "Users");
        }
    }
}
