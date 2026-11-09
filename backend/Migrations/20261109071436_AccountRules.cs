using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SchitayKa.Api.Migrations
{
    /// <inheritdoc />
    public partial class AccountRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    TxType = table.Column<int>(type: "INTEGER", nullable: true),
                    MatchCategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    CommentContains = table.Column<string>(type: "TEXT", nullable: true),
                    AmountMin = table.Column<decimal>(type: "TEXT", nullable: true),
                    AmountMax = table.Column<decimal>(type: "TEXT", nullable: true),
                    SetCategoryId = table.Column<int>(type: "INTEGER", nullable: true),
                    Threshold = table.Column<decimal>(type: "TEXT", nullable: true),
                    DefaultReturnDays = table.Column<int>(type: "INTEGER", nullable: true),
                    NotifyInApp = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountRules_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountRules_Categories_MatchCategoryId",
                        column: x => x.MatchCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountRules_Categories_SetCategoryId",
                        column: x => x.SetCategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountRules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    BalanceSnapshot = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DismissedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountAlerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountAlerts_AccountRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AccountRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountAlerts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AccountAlerts_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReserveLoans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", nullable: false),
                    TakenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReturnedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TransactionId = table.Column<int>(type: "INTEGER", nullable: true),
                    Comment = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReserveLoans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReserveLoans_AccountRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AccountRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReserveLoans_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReserveLoans_Transactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "Transactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ReserveLoans_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountAlerts_AccountId",
                table: "AccountAlerts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAlerts_RuleId",
                table: "AccountAlerts",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountAlerts_UserId_AccountId_ResolvedAt",
                table: "AccountAlerts",
                columns: new[] { "UserId", "AccountId", "ResolvedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountRules_AccountId",
                table: "AccountRules",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRules_MatchCategoryId",
                table: "AccountRules",
                column: "MatchCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRules_SetCategoryId",
                table: "AccountRules",
                column: "SetCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountRules_UserId_Priority",
                table: "AccountRules",
                columns: new[] { "UserId", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ReserveLoans_AccountId",
                table: "ReserveLoans",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveLoans_RuleId",
                table: "ReserveLoans",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveLoans_TransactionId",
                table: "ReserveLoans",
                column: "TransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveLoans_UserId_ReturnedAt_DueDate",
                table: "ReserveLoans",
                columns: new[] { "UserId", "ReturnedAt", "DueDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountAlerts");

            migrationBuilder.DropTable(
                name: "ReserveLoans");

            migrationBuilder.DropTable(
                name: "AccountRules");
        }
    }
}
