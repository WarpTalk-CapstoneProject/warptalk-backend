using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FixPendingModelChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_subscription_credits",
                schema: "subscription",
                table: "subscriptions");

            migrationBuilder.RenameColumn(
                name: "CancelAtPeriodEnd",
                schema: "subscription",
                table: "subscriptions",
                newName: "cancel_at_period_end");

            migrationBuilder.AlterColumn<bool>(
                name: "cancel_at_period_end",
                schema: "subscription",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.AddCheckConstraint(
                name: "chk_subscription_credits",
                schema: "subscription",
                table: "subscriptions",
                sql: "credits_remaining >= -2147483648");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "chk_subscription_credits",
                schema: "subscription",
                table: "subscriptions");

            migrationBuilder.RenameColumn(
                name: "cancel_at_period_end",
                schema: "subscription",
                table: "subscriptions",
                newName: "CancelAtPeriodEnd");

            migrationBuilder.AlterColumn<bool>(
                name: "CancelAtPeriodEnd",
                schema: "subscription",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "chk_subscription_credits",
                schema: "subscription",
                table: "subscriptions",
                sql: "credits_remaining >= 0");
        }
    }
}
