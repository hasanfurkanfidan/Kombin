using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Engagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "engagement");

            migrationBuilder.AddColumn<string>(
                name: "affiliate_params",
                schema: "catalog",
                table: "sellers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "affiliate_url_template",
                schema: "catalog",
                table: "sellers",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "clicks",
                schema: "engagement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    persona_id = table.Column<Guid>(type: "uuid", nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    user_agent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ip_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    redirect_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    clicked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_clicks", x => x.id);
                    table.ForeignKey(
                        name: "fk_clicks_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clicks_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clicks_sellers_seller_id",
                        column: x => x.seller_id,
                        principalSchema: "catalog",
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_clicks_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "price_alerts",
                schema: "engagement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    target_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    sizes = table.Column<string[]>(type: "text[]", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_triggered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    trigger_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_price_alerts", x => x.id);
                    table.ForeignKey(
                        name: "fk_price_alerts_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_price_alerts_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "saved_products",
                schema: "engagement",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_saved_products", x => new { x.user_id, x.product_id });
                    table.ForeignKey(
                        name: "fk_saved_products_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_saved_products_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_triggers",
                schema: "engagement",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    price_alert_id = table.Column<Guid>(type: "uuid", nullable: true),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    triggered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    price_before = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    price_after = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_alert_triggers", x => x.id);
                    table.ForeignKey(
                        name: "fk_alert_triggers_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_triggers_price_alerts_price_alert_id",
                        column: x => x.price_alert_id,
                        principalSchema: "engagement",
                        principalTable: "price_alerts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_alert_triggers_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_alert_triggers_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "sellers",
                keyColumn: "id",
                keyValue: new Guid("5e110000-0000-7000-8000-000000000001"),
                columns: new[] { "affiliate_params", "affiliate_url_template" },
                values: new object[] { null, null });

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "sellers",
                keyColumn: "id",
                keyValue: new Guid("5e110000-0000-7000-8000-000000000002"),
                columns: new[] { "affiliate_params", "affiliate_url_template" },
                values: new object[] { null, null });

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_offer_id",
                schema: "engagement",
                table: "alert_triggers",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_price_alert_id",
                schema: "engagement",
                table: "alert_triggers",
                column: "price_alert_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_product_id",
                schema: "engagement",
                table: "alert_triggers",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_user_id_triggered_at",
                schema: "engagement",
                table: "alert_triggers",
                columns: new[] { "user_id", "triggered_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_clicks_offer_id_clicked_at",
                schema: "engagement",
                table: "clicks",
                columns: new[] { "offer_id", "clicked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_clicks_product_id",
                schema: "engagement",
                table: "clicks",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_clicks_seller_id",
                schema: "engagement",
                table: "clicks",
                column: "seller_id");

            migrationBuilder.CreateIndex(
                name: "ix_clicks_user_id_clicked_at",
                schema: "engagement",
                table: "clicks",
                columns: new[] { "user_id", "clicked_at" });

            migrationBuilder.CreateIndex(
                name: "ix_price_alerts_product_id_is_active",
                schema: "engagement",
                table: "price_alerts",
                columns: new[] { "product_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_price_alerts_user_id_created_at",
                schema: "engagement",
                table: "price_alerts",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_price_alerts_user_id_product_id_kind",
                schema: "engagement",
                table: "price_alerts",
                columns: new[] { "user_id", "product_id", "kind" },
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_saved_products_product_id",
                schema: "engagement",
                table: "saved_products",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_saved_products_user_id_created_at",
                schema: "engagement",
                table: "saved_products",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "alert_triggers",
                schema: "engagement");

            migrationBuilder.DropTable(
                name: "clicks",
                schema: "engagement");

            migrationBuilder.DropTable(
                name: "saved_products",
                schema: "engagement");

            migrationBuilder.DropTable(
                name: "price_alerts",
                schema: "engagement");

            migrationBuilder.DropColumn(
                name: "affiliate_params",
                schema: "catalog",
                table: "sellers");

            migrationBuilder.DropColumn(
                name: "affiliate_url_template",
                schema: "catalog",
                table: "sellers");
        }
    }
}
