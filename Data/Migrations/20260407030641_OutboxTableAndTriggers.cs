using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class OutboxTableAndTriggers : Migration
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
            migrationBuilder.Sql("CREATE VIEW Outbox AS SELECT * FROM \"Tasks\" WHERE \"AckedAt\" IS NULL AND \"SentAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS insert_trigger ON Tasks;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS notify_change;");
            migrationBuilder.Sql("DROP VIEW Outbox");
        }
    }
}
