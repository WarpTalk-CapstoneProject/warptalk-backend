using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "allow_acl",
                schema: "subscription",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "allow_glossary",
                schema: "subscription",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "voice_clone_limit_mins",
                schema: "subscription",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "allow_acl",
                schema: "subscription",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "allow_glossary",
                schema: "subscription",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "voice_clone_limit_mins",
                schema: "subscription",
                table: "plans");
        }
    }
}
