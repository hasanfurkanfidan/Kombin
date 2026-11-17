using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TryOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "tryon");

            migrationBuilder.CreateTable(
                name: "garments",
                schema: "tryon",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    wardrobe_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    prepared_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prepare_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    reliable = table.Column<bool>(type: "boolean", nullable: false),
                    may_be_outermost = table.Column<bool>(type: "boolean", nullable: true),
                    sleeves = table.Column<string>(type: "jsonb", nullable: true),
                    flags = table.Column<string[]>(type: "text[]", nullable: false),
                    landmarks = table.Column<string>(type: "jsonb", nullable: true),
                    cutout_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    image_index = table.Column<int>(type: "integer", nullable: true),
                    prepared_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_garments", x => x.id);
                    table.CheckConstraint("ck_garments_attempts", "attempts >= 0");
                    table.CheckConstraint("ck_garments_owner", "(wardrobe_item_id IS NULL) <> (product_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_garments_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_garments_wardrobe_items_wardrobe_item_id",
                        column: x => x.wardrobe_item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "renders",
                schema: "tryon",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cache_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    body_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    renderer = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    image_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    refusal = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    width = table.Column<int>(type: "integer", nullable: false),
                    height = table.Column<int>(type: "integer", nullable: false),
                    garments = table.Column<string>(type: "jsonb", nullable: false),
                    defaulted_measurements = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_renders", x => x.id);
                    table.CheckConstraint("ck_renders_image_or_refusal", "(image_key IS NULL) <> (refusal IS NULL)");
                    table.ForeignKey(
                        name: "fk_renders_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "tryon",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    garment_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    renderer = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports1", x => x.id);
                    table.CheckConstraint("ck_reports_subject", "(outfit_id IS NULL) <> (product_id IS NULL)");
                    table.ForeignKey(
                        name: "fk_reports_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_garments_prepared_at",
                schema: "tryon",
                table: "garments",
                column: "prepared_at");

            migrationBuilder.CreateIndex(
                name: "ix_garments_product_id",
                schema: "tryon",
                table: "garments",
                column: "product_id",
                unique: true,
                filter: "product_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_garments_wardrobe_item_id",
                schema: "tryon",
                table: "garments",
                column: "wardrobe_item_id",
                unique: true,
                filter: "wardrobe_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_renders_last_used_at",
                schema: "tryon",
                table: "renders",
                column: "last_used_at");

            migrationBuilder.CreateIndex(
                name: "ix_renders_user_id_cache_key",
                schema: "tryon",
                table: "renders",
                columns: new[] { "user_id", "cache_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_user_id_outfit_id_product_id_renderer",
                schema: "tryon",
                table: "reports",
                columns: new[] { "user_id", "outfit_id", "product_id", "renderer" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "garments",
                schema: "tryon");

            migrationBuilder.DropTable(
                name: "renders",
                schema: "tryon");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "tryon");
        }
    }
}
