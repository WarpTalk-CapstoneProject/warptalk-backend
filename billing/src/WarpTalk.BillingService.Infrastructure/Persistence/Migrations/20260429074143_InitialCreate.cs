using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace WarpTalk.BillingService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "billing");

            migrationBuilder.CreateTable(
                name: "QuotaAuditLogs",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ReferenceId = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotaAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPlans",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    BaseQuotaMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    PriceVnd = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    MaxParticipants = table.Column<int>(type: "integer", nullable: false),
                    FeaturesJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderCode = table.Column<long>(type: "bigint", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountVnd = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PurchasedMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PayOsTransactionId = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageQuotas",
                schema: "billing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    TotalAllocatedMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    ConsumedMinutes = table.Column<decimal>(type: "numeric(18,4)", nullable: false),
                    CycleStartDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CycleEndDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageQuotas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UsageQuotas_SubscriptionPlans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "billing",
                        principalTable: "SubscriptionPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "billing",
                table: "SubscriptionPlans",
                columns: new[] { "Id", "BaseQuotaMinutes", "CreatedAt", "FeaturesJson", "IsActive", "MaxParticipants", "Name", "PriceVnd", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("11111111-1111-1111-1111-111111111111"), 30m, new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(6406), "{}", true, 5, "Free", 0m, null },
                    { new Guid("22222222-2222-2222-2222-222222222222"), 500m, new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7238), "{}", true, 25, "Pro", 199000m, null },
                    { new Guid("33333333-3333-3333-3333-333333333333"), 1000m, new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7244), "{}", true, 100, "Premium", 499000m, null },
                    { new Guid("44444444-4444-4444-4444-444444444444"), 10000m, new DateTime(2026, 4, 29, 7, 41, 43, 388, DateTimeKind.Utc).AddTicks(7254), "{}", true, 1000, "Enterprise", 0m, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuotaAuditLogs_ReferenceId",
                schema: "billing",
                table: "QuotaAuditLogs",
                column: "ReferenceId");

            migrationBuilder.CreateIndex(
                name: "IX_QuotaAuditLogs_WorkspaceId",
                schema: "billing",
                table: "QuotaAuditLogs",
                column: "WorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_OrderCode",
                schema: "billing",
                table: "Transactions",
                column: "OrderCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageQuotas_PlanId",
                schema: "billing",
                table: "UsageQuotas",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageQuotas_WorkspaceId",
                schema: "billing",
                table: "UsageQuotas",
                column: "WorkspaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuotaAuditLogs",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "Transactions",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "UsageQuotas",
                schema: "billing");

            migrationBuilder.DropTable(
                name: "SubscriptionPlans",
                schema: "billing");
        }
    }
}
