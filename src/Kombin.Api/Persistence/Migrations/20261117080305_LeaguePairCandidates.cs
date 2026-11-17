using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeaguePairCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_entries_day_status_kind_exposures_id",
                schema: "league",
                table: "entries",
                columns: new[] { "day", "status", "kind", "exposures", "id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_entries_day_status_kind_exposures_id",
                schema: "league",
                table: "entries");
        }
    }
}
