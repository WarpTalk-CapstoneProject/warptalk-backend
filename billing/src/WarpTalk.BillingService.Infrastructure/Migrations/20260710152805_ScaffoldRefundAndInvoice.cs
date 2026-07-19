using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScaffoldRefundAndInvoice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_invoices_payment",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropForeignKey(
                name: "fk_invoices_subscription",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "idx_invoices_stripe_id",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropIndex(
                name: "IX_invoices_subscription_id",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "hosted_invoice_url",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "invoice_pdf_url",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "stripe_invoice_id",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "subscription_id",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.RenameColumn(
                name: "workspace_id",
                schema: "subscription",
                table: "invoices",
                newName: "user_id");

            migrationBuilder.RenameColumn(
                name: "amount",
                schema: "subscription",
                table: "invoices",
                newName: "total");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "subscription",
                table: "invoices",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "issued",
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<Guid>(
                name: "payment_id",
                schema: "subscription",
                table: "invoices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                schema: "subscription",
                table: "invoices",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "VND",
                oldClrType: typeof(string),
                oldType: "character varying(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<DateTime>(
                name: "due_at",
                schema: "subscription",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_number",
                schema: "subscription",
                table: "invoices",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "issued_at",
                schema: "subscription",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<string>(
                name: "line_items",
                schema: "subscription",
                table: "invoices",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at",
                schema: "subscription",
                table: "invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pdf_url",
                schema: "subscription",
                table: "invoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "subtotal",
                schema: "subscription",
                table: "invoices",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "tax",
                schema: "subscription",
                table: "invoices",
                type: "numeric(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "refunds",
                schema: "subscription",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "uuidv7()"),
                    payment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pending"),
                    provider_refund_id = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
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
                name: "IX_refunds_payment_id",
                schema: "subscription",
                table: "refunds",
                column: "payment_id");

            migrationBuilder.AddForeignKey(
                name: "invoices_payment_id_fkey",
                schema: "subscription",
                table: "invoices",
                column: "payment_id",
                principalSchema: "subscription",
                principalTable: "payments",
                principalColumn: "id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "invoices_payment_id_fkey",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropTable(
                name: "refunds",
                schema: "subscription");

            migrationBuilder.DropIndex(
                name: "invoices_invoice_number_key",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "due_at",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "invoice_number",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "issued_at",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "line_items",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "paid_at",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "pdf_url",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "subtotal",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "tax",
                schema: "subscription",
                table: "invoices");

            migrationBuilder.RenameColumn(
                name: "user_id",
                schema: "subscription",
                table: "invoices",
                newName: "workspace_id");

            migrationBuilder.RenameColumn(
                name: "total",
                schema: "subscription",
                table: "invoices",
                newName: "amount");

            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "subscription",
                table: "invoices",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(20)",
                oldMaxLength: 20,
                oldDefaultValue: "issued");

            migrationBuilder.AlterColumn<Guid>(
                name: "payment_id",
                schema: "subscription",
                table: "invoices",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "currency",
                schema: "subscription",
                table: "invoices",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(3)",
                oldMaxLength: 3,
                oldDefaultValue: "VND");

            migrationBuilder.AddColumn<string>(
                name: "hosted_invoice_url",
                schema: "subscription",
                table: "invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invoice_pdf_url",
                schema: "subscription",
                table: "invoices",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "stripe_invoice_id",
                schema: "subscription",
                table: "invoices",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "subscription_id",
                schema: "subscription",
                table: "invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "idx_invoices_stripe_id",
                schema: "subscription",
                table: "invoices",
                column: "stripe_invoice_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_subscription_id",
                schema: "subscription",
                table: "invoices",
                column: "subscription_id");

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_payment",
                schema: "subscription",
                table: "invoices",
                column: "payment_id",
                principalSchema: "subscription",
                principalTable: "payments",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_invoices_subscription",
                schema: "subscription",
                table: "invoices",
                column: "subscription_id",
                principalSchema: "subscription",
                principalTable: "subscriptions",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
