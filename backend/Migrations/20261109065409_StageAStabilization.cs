using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class StageAStabilization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Старый индекс мог называться иначе или отсутствовать в legacy-базе.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_BudgetLimits_UserId_CategoryId_Month\";");

            migrationBuilder.AddColumn<bool>(
                name: "IsTransfer",
                table: "Transactions",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Бэкфилл: в старых демо-данных переводы уже есть, но без признака.
            // Это не меняет балансы, зато убирает их из доходов/расходов и рекомендаций.
            migrationBuilder.Sql(
                "UPDATE \"Transactions\" SET \"IsTransfer\" = 1 " +
                "WHERE \"Comment\" LIKE 'Перевод%' OR \"Comment\" = 'Погашение кредитной карты';");

            // Дедупликация перед созданием нового уникального индекса
            // (старый индекс не пропускал дубли, но legacy-база могла быть изменена вручную).
            migrationBuilder.Sql(
                "DELETE FROM \"BudgetLimits\" WHERE \"Id\" NOT IN (" +
                "SELECT MAX(\"Id\") FROM \"BudgetLimits\" " +
                "GROUP BY \"UserId\", \"CategoryId\", \"Year\", \"Month\");");

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_BudgetLimits_UserId_CategoryId_Year_Month\" " +
                "ON \"BudgetLimits\" (\"UserId\", \"CategoryId\", \"Year\", \"Month\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BudgetLimits_UserId_CategoryId_Year_Month",
                table: "BudgetLimits");

            migrationBuilder.DropColumn(
                name: "IsTransfer",
                table: "Transactions");

            migrationBuilder.CreateIndex(
                name: "IX_BudgetLimits_UserId_CategoryId_Month",
                table: "BudgetLimits",
                columns: new[] { "UserId", "CategoryId", "Month" },
                unique: true);
        }
    }
}
