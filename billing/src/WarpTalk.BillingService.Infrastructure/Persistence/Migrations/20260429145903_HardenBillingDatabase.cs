using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenBillingDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QuotaAuditLogs_ReferenceId",
                schema: "billing",
                table: "QuotaAuditLogs");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                schema: "billing",
                table: "UsageQuotas",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "billing",
                table: "UsageQuotas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("11111111-1111-1111-1111-111111111111"),
                column: "CreatedAt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("22222222-2222-2222-2222-222222222222"),
                column: "CreatedAt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("33333333-3333-3333-3333-333333333333"),
                column: "CreatedAt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.UpdateData(
                schema: "billing",
                table: "SubscriptionPlans",
                keyColumn: "Id",
                keyValue: new Guid("44444444-4444-4444-4444-444444444444"),
                column: "CreatedAt",
                value: new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc));

            migrationBuilder.CreateIndex(
                name: "IX_QuotaAuditLogs_WorkspaceId_ReferenceId",
                schema: "billing",
                table: "QuotaAuditLogs",
                columns: new[] { "WorkspaceId", "ReferenceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QuotaAuditLogs_WorkspaceId_ReferenceId",
                schema: "billing",
                table: "QuotaAuditLogs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "billing",
                table: "UsageQuotas");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "billing",
                table: "UsageQuotas");

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

            migrationBuilder.CreateIndex(
                name: "IX_QuotaAuditLogs_ReferenceId",
                schema: "billing",
                table: "QuotaAuditLogs",
                column: "ReferenceId");
        }
    }
}
