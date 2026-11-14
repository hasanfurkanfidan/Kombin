using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrustedReporters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "counts_toward_hide",
                schema: "social",
                table: "reports",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "counts_toward_hide",
                schema: "social",
                table: "reports");
        }
    }
}
