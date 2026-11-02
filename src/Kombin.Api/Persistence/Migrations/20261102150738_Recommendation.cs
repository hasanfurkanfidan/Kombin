using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Recommendation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "recommendation");

            migrationBuilder.AddColumn<string>(
                name: "city",
                schema: "profile",
                table: "user_profiles",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "narrations",
                schema: "recommendation",
                columns: table => new
                {
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    text = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_narrations", x => x.hash);
                });

            migrationBuilder.CreateTable(
                name: "outfits",
                schema: "recommendation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    persona_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    completeness = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occasion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    for_date = table.Column<DateOnly>(type: "date", nullable: false),
                    weather_json = table.Column<string>(type: "jsonb", nullable: true),
                    seed = table.Column<int>(type: "integer", nullable: false),
                    score = table.Column<int>(type: "integer", nullable: false),
                    rule = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    narration = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    narration_provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    narration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    narration_input = table.Column<string>(type: "jsonb", nullable: false),
                    saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    worn_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismissed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    dismiss_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfits", x => x.id);
                    table.CheckConstraint("ck_outfits_score", "score BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_outfits_personas_persona_id",
                        column: x => x.persona_id,
                        principalSchema: "profile",
                        principalTable: "personas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outfits_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outfit_feedback",
                schema: "recommendation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfit_feedback", x => x.id);
                    table.ForeignKey(
                        name: "fk_outfit_feedback_outfits_outfit_id",
                        column: x => x.outfit_id,
                        principalSchema: "recommendation",
                        principalTable: "outfits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outfit_feedback_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outfit_items",
                schema: "recommendation",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    wardrobe_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outfit_items", x => x.id);
                    table.CheckConstraint("ck_outfit_items_source", "(wardrobe_item_id IS NULL) <> (product_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_outfit_items_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_outfit_items_outfits_outfit_id",
                        column: x => x.outfit_id,
                        principalSchema: "recommendation",
                        principalTable: "outfits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outfit_items_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_outfit_items_wardrobe_items_wardrobe_item_id",
                        column: x => x.wardrobe_item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_products_category_id_primary_color_formality",
                schema: "catalog",
                table: "products",
                columns: new[] { "category_id", "primary_color", "formality" },
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_feedback_outfit_id",
                schema: "recommendation",
                table: "outfit_feedback",
                column: "outfit_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_feedback_user_id_created_at",
                schema: "recommendation",
                table: "outfit_feedback",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_outfit_items_offer_id",
                schema: "recommendation",
                table: "outfit_items",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_items_outfit_id",
                schema: "recommendation",
                table: "outfit_items",
                column: "outfit_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_items_product_id",
                schema: "recommendation",
                table: "outfit_items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfit_items_wardrobe_item_id",
                schema: "recommendation",
                table: "outfit_items",
                column: "wardrobe_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfits_persona_id",
                schema: "recommendation",
                table: "outfits",
                column: "persona_id");

            migrationBuilder.CreateIndex(
                name: "ix_outfits_user_id_hash",
                schema: "recommendation",
                table: "outfits",
                columns: new[] { "user_id", "hash" });

            migrationBuilder.CreateIndex(
                name: "ix_outfits_user_id_saved_at",
                schema: "recommendation",
                table: "outfits",
                columns: new[] { "user_id", "saved_at" },
                filter: "saved_at IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "narrations",
                schema: "recommendation");

            migrationBuilder.DropTable(
                name: "outfit_feedback",
                schema: "recommendation");

            migrationBuilder.DropTable(
                name: "outfit_items",
                schema: "recommendation");

            migrationBuilder.DropTable(
                name: "outfits",
                schema: "recommendation");

            migrationBuilder.DropIndex(
                name: "ix_products_category_id_primary_color_formality",
                schema: "catalog",
                table: "products");

            migrationBuilder.DropColumn(
                name: "city",
                schema: "profile",
                table: "user_profiles");
        }
    }
}
