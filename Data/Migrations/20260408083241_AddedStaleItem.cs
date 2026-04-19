using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Data.Migrations
{
    /// <inheritdoc />
    public partial class AddedStaleItem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE VIEW StaleTasks AS SELECT * FROM \"Tasks\" WHERE \"AckedAt\" IS NULL AND \"SentAt\" + INTERVAL '30 seconds' + (\"Retries\" * INTERVAL '1 minute') < clock_timestamp()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW StaleTasks");
        }
    }    
}
