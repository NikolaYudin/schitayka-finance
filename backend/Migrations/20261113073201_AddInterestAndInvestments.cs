using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInterestAndInvestments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InterestMode",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "InterestRate",
                table: "Accounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "InterestTaxRate",
                table: "Accounts",
                type: "TEXT",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "MaturesAt",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "OpenedAt",
                table: "Accounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Banks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Code = table.Column<string>(type: "TEXT", nullable: false),
                    Country = table.Column<string>(type: "TEXT", nullable: false),
                    IsSystem = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Banks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InterestAccruals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    From = table.Column<DateTime>(type: "TEXT", nullable: false),
                    To = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Days = table.Column<int>(type: "INTEGER", nullable: false),
                    Gross = table.Column<decimal>(type: "TEXT", nullable: false),
                    Tax = table.Column<decimal>(type: "TEXT", nullable: false),
                    Net = table.Column<decimal>(type: "TEXT", nullable: false),
                    IsPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterestAccruals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InterestAccruals_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InterestAccruals_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestmentPortfolios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    BankId = table.Column<int>(type: "INTEGER", nullable: false),
                    Product = table.Column<int>(type: "INTEGER", nullable: false),
                    Strategy = table.Column<int>(type: "INTEGER", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentPortfolios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentPortfolios_Banks_BankId",
                        column: x => x.BankId,
                        principalTable: "Banks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvestmentPortfolios_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InvestmentHoldings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    PortfolioId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Ticker = table.Column<string>(type: "TEXT", nullable: false),
                    AssetType = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", nullable: false),
                    AvgPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvestmentHoldings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvestmentHoldings_InvestmentPortfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "InvestmentPortfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvestmentHoldings_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Banks_UserId_Code",
                table: "Banks",
                columns: new[] { "UserId", "Code" });

            migrationBuilder.CreateIndex(
                name: "IX_InterestAccruals_AccountId",
                table: "InterestAccruals",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InterestAccruals_UserId_AccountId_From",
                table: "InterestAccruals",
                columns: new[] { "UserId", "AccountId", "From" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentHoldings_PortfolioId_Name",
                table: "InvestmentHoldings",
                columns: new[] { "PortfolioId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentHoldings_UserId",
                table: "InvestmentHoldings",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPortfolios_BankId",
                table: "InvestmentPortfolios",
                column: "BankId");

            migrationBuilder.CreateIndex(
                name: "IX_InvestmentPortfolios_UserId_Name",
                table: "InvestmentPortfolios",
                columns: new[] { "UserId", "Name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InterestAccruals");

            migrationBuilder.DropTable(
                name: "InvestmentHoldings");

            migrationBuilder.DropTable(
                name: "InvestmentPortfolios");

            migrationBuilder.DropTable(
                name: "Banks");

            migrationBuilder.DropColumn(
                name: "InterestMode",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InterestRate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "InterestTaxRate",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "MaturesAt",
                table: "Accounts");

            migrationBuilder.DropColumn(
                name: "OpenedAt",
                table: "Accounts");
        }
    }
}
