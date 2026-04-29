using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class IntialCommit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conflicts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyId = table.Column<string>(type: "text", nullable: false),
                    FailedAt = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()"),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conflicts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Successes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IdempotencyId = table.Column<string>(type: "text", nullable: true),
                    FinishedAt = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Successes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "int8", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyId = table.Column<string>(type: "text", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamptz", nullable: false, defaultValueSql: "clock_timestamp()"),
                    SentAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    PublishedAt = table.Column<DateTime>(type: "timestamptz", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamptz", nullable: true, defaultValueSql: "clock_timestamp()"),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conflicts_IdempotencyId",
                table: "Conflicts",
                column: "IdempotencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Successes_IdempotencyId",
                table: "Successes",
                column: "IdempotencyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_IdempotencyId",
                table: "Tasks",
                column: "IdempotencyId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_TaskId",
                table: "Tasks",
                column: "TaskId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Conflicts");

            migrationBuilder.DropTable(
                name: "Successes");

            migrationBuilder.DropTable(
                name: "Tasks");
        }
    }
}
