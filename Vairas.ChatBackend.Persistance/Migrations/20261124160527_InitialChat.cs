using Microsoft.EntityFrameworkCore.Migrations;
namespace Vairas.ChatBackend.Persistance.Migrations;
public partial class InitialChat : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(Sql.Read("baseline.sql"));
        migrationBuilder.Sql(Sql.Read("amendments.sql"));
        migrationBuilder.Sql(Sql.Read("guards.sql"));
    }
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Destructive chat-history rollback requires a separately reviewed erasure procedure.");
}
