using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TelegramDownloader.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_sessions",
                columns: table => new
                {
                    telegram_user_id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    phone_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    session_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_sessions", x => x.telegram_user_id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_sessions");
        }
    }
}
