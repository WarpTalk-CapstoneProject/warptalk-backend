using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WarpTalk.BillingService.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIdempotencyConstraintToCreditTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_credit_transactions_correlation_type",
                schema: "subscription",
                table: "credit_transactions",
                columns: new[] { "correlation_id", "type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_credit_transactions_correlation_type",
                schema: "subscription",
                table: "credit_transactions");
        }
    }
}
