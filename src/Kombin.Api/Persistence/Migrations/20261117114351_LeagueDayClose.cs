using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeagueDayClose : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "removed_by_review",
                schema: "league",
                table: "daily_scores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "under_review",
                schema: "league",
                table: "daily_scores",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "voter_days",
                schema: "league",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    served = table.Column<int>(type: "integer", nullable: false),
                    counted = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_voter_days", x => new { x.user_id, x.day });
                    table.CheckConstraint("ck_voter_days_counts", "served >= 0 AND counted >= 0 AND counted <= served");
                    table.ForeignKey(
                        name: "fk_voter_days_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_voter_days_day",
                schema: "league",
                table: "voter_days",
                column: "day");

            // The counters of the days played before the table existed, from their pair rows (the league is off in production, so normally none).
            migrationBuilder.Sql("""
                INSERT INTO league.voter_days (user_id, day, served, counted)
                SELECT voter_id, day, max(sequence), count(*) FILTER (WHERE counted)
                FROM league.pairs
                GROUP BY voter_id, day
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "voter_days",
                schema: "league");

            migrationBuilder.DropColumn(
                name: "removed_by_review",
                schema: "league",
                table: "daily_scores");

            migrationBuilder.DropColumn(
                name: "under_review",
                schema: "league",
                table: "daily_scores");
        }
    }
}
