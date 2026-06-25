using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class ViewsAndTriggers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
             migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION notify_change()
                RETURNS trigger AS $$
                BEGIN
                    PERFORM pg_notify('task_channel', row_to_json(NEW)::text);
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
            ");

            migrationBuilder.Sql("CREATE TRIGGER insert_trigger AFTER INSERT ON \"Tasks\" FOR EACH ROW EXECUTE FUNCTION notify_change();");
            migrationBuilder.Sql("CREATE VIEW Outbox AS SELECT \"Id\", \"TaskId\", \"CorrelationId\", \"IdempotencyId\", \"Url\", \"CreatedAt\", \"SentAt\", \"PublishedAt\", \"NextAttemptAt\", \"Attempt\", \"SentByToken\" FROM \"Tasks\" WHERE \"SentAt\" IS NULL AND \"NextAttemptAt\" <= clock_timestamp() AND \"Attempt\" < 5 AND NOT EXISTS (SELECT 1 FROM \"Successes\" WHERE \"IdempotencyId\" = \"Tasks\".\"IdempotencyId\")");
            migrationBuilder.Sql("CREATE VIEW StaleTasks AS SELECT \"Id\", \"TaskId\", \"CorrelationId\", \"IdempotencyId\", \"Url\", \"CreatedAt\", \"SentAt\", \"PublishedAt\", \"NextAttemptAt\", \"Attempt\", \"SentByToken\" FROM \"Tasks\" WHERE \"SentAt\" IS NOT NULL AND \"SentAt\" < clock_timestamp() - INTERVAL '120 seconds' AND NOT EXISTS (SELECT 1 FROM \"Successes\" WHERE \"IdempotencyId\" = \"Tasks\".\"IdempotencyId\") AND NOT EXISTS (SELECT 1 FROM \"Conflicts\" WHERE \"TaskId\" = \"Tasks\".\"TaskId\" AND \"Attempt\" = \"Tasks\".\"Attempt\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS insert_trigger ON Tasks;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notify_change;");
            migrationBuilder.Sql("DROP VIEW Outbox");
            migrationBuilder.Sql("DROP VIEW StaleTasks");
        }
    }
}
