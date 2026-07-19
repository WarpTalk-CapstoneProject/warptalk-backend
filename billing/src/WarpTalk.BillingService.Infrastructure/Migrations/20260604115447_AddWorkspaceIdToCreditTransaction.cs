using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkspaceIdToCreditTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoices",
                schema: "subscription");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "subscription");

            migrationBuilder.AlterColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "usage_records",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                comment: "External AuthService workspace id. No physical FK.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldComment: "External AuthService workspace id. No physical FK.");

            migrationBuilder.AlterColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "subscriptions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                comment: "External AuthService workspace id. No physical FK.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true,
                oldComment: "External AuthService workspace id. No physical FK.");

            migrationBuilder.AddColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "payments",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                comment: "External AuthService workspace id. No physical FK.");

            migrationBuilder.AddColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "credit_transactions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                comment: "External AuthService workspace id. No physical FK.");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "workspace_id",
                schema: "subscription",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "workspace_id",
                schema: "subscription",
                table: "credit_transactions");

            migrationBuilder.AlterColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "usage_records",
                type: "uuid",
                nullable: true,
                comment: "External AuthService workspace id. No physical FK.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldComment: "External AuthService workspace id. No physical FK.");

            migrationBuilder.AlterColumn<Guid>(
                name: "workspace_id",
                schema: "subscription",
                table: "subscriptions",
                type: "uuid",
                nullable: true,
                comment: "External AuthService workspace id. No physical FK.",
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldComment: "External AuthService workspace id. No physical FK.");

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "VND"),
                    due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    invoice_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    issued_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    line_items = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    paid_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    pdf_url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "issued"),
                    subtotal = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    tax = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false, defaultValue: 0m),
                    total = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "External AuthService user id. No physical FK.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("invoices_pkey", x => x.id);
                    table.ForeignKey(
                        name: "invoices_payment_id_fkey",
                        column: x => x.payment_id,
                        principalSchema: "subscription",
                        principalTable: "payments",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    provider_refund_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false, comment: "External AuthService user id. No physical FK.")
                },
                constraints: table =>
                {
                    table.PrimaryKey("refunds_pkey", x => x.id);
                    table.ForeignKey(
                        name: "refunds_payment_id_fkey",
                        column: x => x.payment_id,
                        principalSchema: "subscription",
                        principalTable: "payments",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "invoices_invoice_number_key",
                schema: "subscription",
                table: "invoices",
                column: "invoice_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_payment_id",
                schema: "subscription",
                table: "invoices",
                column: "payment_id");

            migrationBuilder.CreateIndex(
                name: "IX_refunds_payment_id",
                schema: "subscription",
                table: "refunds",
                column: "payment_id");
        }
    }
}
