using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountBankColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BankId",
                table: "Accounts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_BankId",
                table: "Accounts",
                column: "BankId");

            migrationBuilder.AddForeignKey(
                name: "FK_Accounts_Banks_BankId",
                table: "Accounts",
                column: "BankId",
                principalTable: "Banks",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Accounts_Banks_BankId",
                table: "Accounts");

            migrationBuilder.DropIndex(
                name: "IX_Accounts_BankId",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "BankId",
                table: "Accounts");
        }
    }
}
