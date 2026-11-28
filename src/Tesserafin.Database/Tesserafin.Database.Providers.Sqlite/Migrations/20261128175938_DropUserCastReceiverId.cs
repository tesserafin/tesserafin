using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tesserafin.Database.Providers.Sqlite.Migrations
{
    /// <inheritdoc />
    public partial class DropUserCastReceiverId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CastReceiverId",
                table: "Users");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CastReceiverId",
                table: "Users",
                type: "TEXT",
                maxLength: 32,
                nullable: true);
        }
    }
}
