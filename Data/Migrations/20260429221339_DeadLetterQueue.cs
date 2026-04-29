using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class DeadLetterQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Retries",
                table: "Tasks",
                newName: "Attempts");

            migrationBuilder.CreateTable(
                name: "DLQ",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyId = table.Column<string>(type: "text", nullable: false),
                    DeadAt = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DLQ", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DLQ_TaskId",
                table: "DLQ",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DLQ");

            migrationBuilder.RenameColumn(
                name: "Attempts",
                table: "Tasks",
                newName: "Retries");
        }
    }
}
