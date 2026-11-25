using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Vairas.ChatBackend.Persistance.Migrations
{
    /// <inheritdoc />
    public partial class ArchiveIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "archive_expected_revision",
                table: "conversation",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "archive_request_id",
                table: "conversation",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_conversation_owner_user_id_archive_request_id",
                table: "conversation",
                columns: new[] { "owner_user_id", "archive_request_id" },
                unique: true);
            migrationBuilder.Sql("ALTER TABLE conversation ADD CONSTRAINT conversation_archive_request_ck CHECK ((archive_request_id IS NULL AND archive_expected_revision IS NULL) OR (archive_request_id IS NOT NULL AND archive_expected_revision IS NOT NULL AND archive_expected_revision > 0 AND status='Archived'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Archive idempotency records require an explicit retention and rollback procedure.");
        }
    }
}
