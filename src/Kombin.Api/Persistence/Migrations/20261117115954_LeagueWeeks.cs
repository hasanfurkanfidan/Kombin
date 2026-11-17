using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeagueWeeks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "league_results",
                schema: "notification",
                table: "notification_preferences",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_daily_scores_day_unnotified",
                schema: "league",
                table: "daily_scores",
                column: "day",
                filter: "notified_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_daily_scores_day_unnotified",
                schema: "league",
                table: "daily_scores");

            migrationBuilder.DropColumn(
                name: "league_results",
                schema: "notification",
                table: "notification_preferences");
        }
    }
}
