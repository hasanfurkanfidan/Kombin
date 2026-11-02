using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Wardrobe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "wardrobe");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "items",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secondary_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    pattern = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    formality = table.Column<short>(type: "smallint", nullable: false),
                    seasons = table.Column<short>(type: "smallint", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_catalog = table.Column<bool>(type: "boolean", nullable: false),
                    size = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    purchase_price = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    purchased_at = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    warnings = table.Column<string[]>(type: "text[]", nullable: false),
                    image_original_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_cutout_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_thumb_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    embedding = table.Column<Vector>(type: "vector(512)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    analysis_json = table.Column<string>(type: "jsonb", nullable: true),
                    analysis_attempts = table.Column<int>(type: "integer", nullable: false),
                    process_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    wear_count = table.Column<int>(type: "integer", nullable: false),
                    last_worn_at = table.Column<DateOnly>(type: "date", nullable: true),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_items", x => x.id);
                    table.CheckConstraint("ck_items_formality", "formality BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_items_brands_brand_id",
                        column: x => x.brand_id,
                        principalSchema: "catalog",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_items_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_items_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_items_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wears",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    worn_on = table.Column<DateOnly>(type: "date", nullable: false),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_wears", x => x.id);
                    table.ForeignKey(
                        name: "fk_wears_items_item_id",
                        column: x => x.item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_wears_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_items_archived_at",
                schema: "wardrobe",
                table: "items",
                column: "archived_at",
                filter: "is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_items_brand_id",
                schema: "wardrobe",
                table: "items",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_category_id",
                schema: "wardrobe",
                table: "items",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_product_id",
                schema: "wardrobe",
                table: "items",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_items_user_id_is_archived_category_id",
                schema: "wardrobe",
                table: "items",
                columns: new[] { "user_id", "is_archived", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_wears_item_id_worn_on",
                schema: "wardrobe",
                table: "wears",
                columns: new[] { "item_id", "worn_on" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_wears_user_id_worn_on",
                schema: "wardrobe",
                table: "wears",
                columns: new[] { "user_id", "worn_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "wears",
                schema: "wardrobe");

            migrationBuilder.DropTable(
                name: "items",
                schema: "wardrobe");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
