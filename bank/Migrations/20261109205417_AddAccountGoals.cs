using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleBankMvc.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountGoals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GoalAmount",
                table: "BankAccounts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GoalAmount",
                table: "BankAccounts");
        }
    }
}
