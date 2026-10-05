using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FieldOps.Modules.WorkOrders.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkOrdersOrganizationIdStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkOrders_OrganizationId_Status",
                table: "WorkOrders",
                columns: new[] { "OrganizationId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkOrders_OrganizationId_Status",
                table: "WorkOrders");
        }
    }
}
