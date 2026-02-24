using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgarIA.Web.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectileMassGained : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ProjectileMassGained",
                table: "PlayerGameStats",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProjectileMassGained",
                table: "PlayerGameStats");
        }
    }
}
