using Microsoft.EntityFrameworkCore.Migrations;
namespace Vairas.ChatBackend.Persistance.Migrations;
public partial class ConfirmedLessonActions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(Sql.Read("lesson-actions.sql"));
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Confirmed actions require an explicit retention and rollback procedure.");
}
