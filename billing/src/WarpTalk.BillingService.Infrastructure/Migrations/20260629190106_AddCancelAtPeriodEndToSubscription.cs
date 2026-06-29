using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCancelAtPeriodEndToSubscription : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                schema: "subscription",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                schema: "subscription",
                table: "subscriptions");
        }
    }
}
