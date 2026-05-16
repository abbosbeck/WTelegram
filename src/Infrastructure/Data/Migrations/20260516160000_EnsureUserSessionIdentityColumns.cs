using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Data.Migrations
{
    /// <summary>
    /// The original <c>Initial</c> migration was edited after some environments had
    /// already applied an earlier version of it. Those databases ended up without
    /// the <c>phone_number</c> / <c>display_name</c> columns, and EF won't re-run
    /// an already-applied migration. This migration brings drifted databases back
    /// in line and is a no-op on databases that already have the columns.
    /// </summary>
    public partial class EnsureUserSessionIdentityColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE user_sessions " +
                "ADD COLUMN IF NOT EXISTS phone_number character varying(32) NULL;");

            migrationBuilder.Sql(
                "ALTER TABLE user_sessions " +
                "ADD COLUMN IF NOT EXISTS display_name character varying(256) NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE user_sessions DROP COLUMN IF EXISTS display_name;");
            migrationBuilder.Sql("ALTER TABLE user_sessions DROP COLUMN IF EXISTS phone_number;");
        }
    }
}
