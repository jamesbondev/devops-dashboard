using Microsoft.EntityFrameworkCore.Migrations;

namespace Orders.Infrastructure.Migrations;

public partial class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.CreateTable(name: "Orders", columns: t => new { Id = t.Column<Guid>() });

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("Orders");
}
