using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.NotificationService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddActionUrlToNotificationMessage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "action_url",
                schema: "notification",
                table: "notification_messages",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "action_url",
                schema: "notification",
                table: "notification_messages");
        }
    }
}
