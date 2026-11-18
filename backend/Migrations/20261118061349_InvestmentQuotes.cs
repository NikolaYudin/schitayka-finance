using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class InvestmentQuotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PriceUpdatedAt",
                table: "InvestmentHoldings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QuoteError",
                table: "InvestmentHoldings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuoteSource",
                table: "InvestmentHoldings",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PriceUpdatedAt",
                table: "InvestmentHoldings");

            migrationBuilder.DropColumn(
                name: "QuoteError",
                table: "InvestmentHoldings");

            migrationBuilder.DropColumn(
                name: "QuoteSource",
                table: "InvestmentHoldings");
        }
    }
}
