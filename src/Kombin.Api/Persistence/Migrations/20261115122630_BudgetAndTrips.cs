using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BudgetAndTrips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trips",
                schema: "recommendation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    city = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    start_date = table.Column<DateOnly>(type: "date", nullable: false),
                    end_date = table.Column<DateOnly>(type: "date", nullable: false),
                    request_json = table.Column<string>(type: "jsonb", nullable: false),
                    plan_json = table.Column<string>(type: "jsonb", nullable: false),
                    piece_count = table.Column<int>(type: "integer", nullable: false),
                    covered_days = table.Column<int>(type: "integer", nullable: false),
                    total_days = table.Column<int>(type: "integer", nullable: false),
                    planned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_trips", x => x.id);
                    table.CheckConstraint("ck_trips_dates", "end_date >= start_date");
                    table.ForeignKey(
                        name: "fk_trips_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_budget",
                schema: "catalog",
                table: "products",
                columns: new[] { "category_id", "audience", "formality" },
                filter: "is_active")
                .Annotation("Npgsql:IndexInclude", new[] { "primary_color", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_offers_sellable_price",
                schema: "catalog",
                table: "offers",
                columns: new[] { "product_id", "current_price" },
                filter: "is_active AND stock_status IN ('InStock', 'LowStock')")
                .Annotation("Npgsql:IndexInclude", new[] { "shipping_cost", "free_shipping_threshold" });

            migrationBuilder.CreateIndex(
                name: "ix_trips_user_id_created_at",
                schema: "recommendation",
                table: "trips",
                columns: new[] { "user_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trips",
                schema: "recommendation");

            migrationBuilder.DropIndex(
                name: "ix_products_budget",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropIndex(
                name: "ix_offers_sellable_price",
                schema: "catalog",
                table: "offers");
        }
    }
}
