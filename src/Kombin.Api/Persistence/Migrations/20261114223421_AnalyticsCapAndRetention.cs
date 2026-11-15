using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AnalyticsCapAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_events_anonymous_id_received_at",
                schema: "analytics",
                table: "events",
                columns: new[] { "anonymous_id", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ix_events_received_at",
                schema: "analytics",
                table: "events",
                column: "received_at");

            migrationBuilder.CreateIndex(
                name: "ix_events_user_id_received_at",
                schema: "analytics",
                table: "events",
                columns: new[] { "user_id", "received_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_events_anonymous_id_received_at",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_received_at",
                schema: "analytics",
                table: "events");

            migrationBuilder.DropIndex(
                name: "ix_events_user_id_received_at",
                schema: "analytics",
                table: "events");
        }
    }
}
