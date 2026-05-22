using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLedgerColumnsToCreditTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_events",
                schema: "subscription");

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "subscription",
                table: "credit_transactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "subscription",
                table: "credit_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "committed");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "subscription",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "subscription",
                table: "credit_transactions");

            migrationBuilder.CreateTable(
                name: "webhook_events",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    event_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("webhook_events_pkey", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "idx_webhook_events_provider_event_id",
                schema: "subscription",
                table: "webhook_events",
                columns: new[] { "provider", "event_id" },
                unique: true);
        }
    }
}
