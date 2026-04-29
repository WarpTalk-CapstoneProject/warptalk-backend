using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedDefaultQuota : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 10, 47, 15, 51, DateTimeKind.Utc).AddTicks(6841));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 10, 47, 15, 51, DateTimeKind.Utc).AddTicks(7720));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 10, 47, 15, 51, DateTimeKind.Utc).AddTicks(7726));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 10, 47, 15, 51, DateTimeKind.Utc).AddTicks(7729));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(6406));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7238));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7244));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"),
                column: "CreatedAt",
                value: new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7254));
        }
    }
}
