using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserRoleThemeAndAdmin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedByUserId",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemMaster",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Role",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "Theme",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: "system");

            // Мастер-профиль: служебная запись без пароля, хранит состояние первичной настройки.
            migrationBuilder.Sql(@"INSERT OR IGNORE INTO ""Users""
                (""Email"", ""PasswordHash"", ""PasswordVersion"", ""Name"", ""Role"", ""IsSystemMaster"", ""Theme"", ""StartingBalance"", ""CreatedAt"")
                VALUES ('master@schitayka.local', '', 0, 'Мастер системы', 0, 1, 'system', 0, CURRENT_TIMESTAMP);");

            // Для уже работающих установок: если реального администратора нет,
            // права один раз получает самый ранний пользователь.
            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""Role"" = 0
                WHERE ""Id"" = (SELECT ""Id"" FROM ""Users"" WHERE ""IsSystemMaster"" = 0 ORDER BY ""Id"" LIMIT 1)
                  AND NOT EXISTS (SELECT 1 FROM ""Users"" WHERE ""Role"" = 0 AND ""IsSystemMaster"" = 0);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DELETE FROM ""Users"" WHERE ""Email"" = 'master@schitayka.local' AND ""IsSystemMaster"" = 1;");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsSystemMaster",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Theme",
                table: "Users");
        }
    }
}
