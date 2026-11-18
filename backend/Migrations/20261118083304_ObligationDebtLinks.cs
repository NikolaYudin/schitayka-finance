using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class ObligationDebtLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DebtEventId",
                table: "ObligationPayments",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ObligationPayments_DebtEventId",
                table: "ObligationPayments",
                column: "DebtEventId");

            migrationBuilder.AddForeignKey(
                name: "FK_ObligationPayments_DebtEvents_DebtEventId",
                table: "ObligationPayments",
                column: "DebtEventId",
                principalTable: "DebtEvents",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ObligationPayments_DebtEvents_DebtEventId",
                table: "ObligationPayments");

            migrationBuilder.DropIndex(
                name: "IX_ObligationPayments_DebtEventId",
                table: "ObligationPayments");

            migrationBuilder.DropColumn(
                name: "DebtEventId",
                table: "ObligationPayments");
        }
    }
}
