using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_Tasks",
                table: "Tasks");

            migrationBuilder.RenameTable(
                name: "Tasks",
                newName: "WorkItem");

            migrationBuilder.RenameIndex(
                name: "IX_Tasks_TaskId",
                table: "WorkItem",
                newName: "IX_WorkItem_TaskId");

            migrationBuilder.RenameIndex(
                name: "IX_Tasks_Id",
                table: "WorkItem",
                newName: "IX_WorkItem_Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_WorkItem",
                table: "WorkItem",
                column: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_WorkItem",
                table: "WorkItem");

            migrationBuilder.RenameTable(
                name: "WorkItem",
                newName: "Tasks");

            migrationBuilder.RenameIndex(
                name: "IX_WorkItem_TaskId",
                table: "Tasks",
                newName: "IX_Tasks_TaskId");

            migrationBuilder.RenameIndex(
                name: "IX_WorkItem_Id",
                table: "Tasks",
                newName: "IX_Tasks_Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_Tasks",
                table: "Tasks",
                column: "Id");
        }
    }
}
