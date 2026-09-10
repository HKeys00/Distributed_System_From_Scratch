using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledView : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE VIEW Scheduled AS SELECT \"Id\", \"TaskId\", \"CorrelationId\", \"IdempotencyId\", \"Url\", \"CreatedAt\", \"SentAt\", \"PublishedAt\", \"NextAttemptAt\", \"Attempt\", \"SentByToken\" FROM \"Tasks\" WHERE \"SentAt\" IS NULL AND \"NextAttemptAt\" > clock_timestamp() AND \"Attempt\" < 5 AND NOT EXISTS (SELECT 1 FROM \"Successes\" WHERE \"IdempotencyId\" = \"Tasks\".\"IdempotencyId\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW Scheduled");
        }
    }
}
