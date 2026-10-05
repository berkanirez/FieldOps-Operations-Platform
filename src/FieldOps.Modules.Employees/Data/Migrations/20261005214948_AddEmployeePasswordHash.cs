using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Modules.Employees.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmployeePasswordHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Employees",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.UpdateData(
                table: "Employees",
                keyColumn: "Id",
                keyValue: 1,
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==");

            migrationBuilder.UpdateData(
                table: "Employees",
                keyColumn: "Id",
                keyValue: 2,
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==");

            migrationBuilder.UpdateData(
                table: "Employees",
                keyColumn: "Id",
                keyValue: 3,
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==");

            migrationBuilder.UpdateData(
                table: "Employees",
                keyColumn: "Id",
                keyValue: 4,
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==");

            migrationBuilder.UpdateData(
                table: "Employees",
                keyColumn: "Id",
                keyValue: 5,
                column: "PasswordHash",
                value: "AQAAAAIAAYagAAAAEFltwH87+iIMPmsrb0oQCEjUnlHlF3KgBfdHE7aN/Do0qSz4xqGwNdzsrrn84r/Cyw==");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PasswordHash",
                table: "Employees");
        }
    }
}
