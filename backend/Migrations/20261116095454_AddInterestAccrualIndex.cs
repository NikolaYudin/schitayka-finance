using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddInterestAccrualIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InterestAccruals_UserId",
                table: "InterestAccruals");

            migrationBuilder.CreateIndex(
                name: "IX_InterestAccruals_UserId_AccountId_From",
                table: "InterestAccruals",
                columns: new[] { "UserId", "AccountId", "From" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_InterestAccruals_UserId_AccountId_From",
                table: "InterestAccruals");

            migrationBuilder.CreateIndex(
                name: "IX_InterestAccruals_UserId",
                table: "InterestAccruals",
                column: "UserId");
        }
    }
}
