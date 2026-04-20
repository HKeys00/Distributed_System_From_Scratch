using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class WebCrawlerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Payload",
                table: "Tasks");

            migrationBuilder.RenameColumn(
                name: "TaskType",
                table: "Tasks",
                newName: "Url");

            migrationBuilder.RenameColumn(
                name: "ExecutionType",
                table: "Tasks",
                newName: "IdempotencyId");

            migrationBuilder.AddColumn<DateTime>(
                name: "FailedAt",
                table: "Tasks",
                type: "timestamptz",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedAt",
                table: "Tasks");

            migrationBuilder.RenameColumn(
                name: "Url",
                table: "Tasks",
                newName: "TaskType");

            migrationBuilder.RenameColumn(
                name: "IdempotencyId",
                table: "Tasks",
                newName: "ExecutionType");

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "Tasks",
                type: "jsonb",
                nullable: true);
        }
    }
}
