using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BulkAdd : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_batches",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    original_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    process_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    refusal = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    failure_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    failure_detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    analysis_attempts = table.Column<int>(type: "integer", nullable: false),
                    unseparated_count = table.Column<int>(type: "integer", nullable: false),
                    unseparated_regions = table.Column<string>(type: "jsonb", nullable: true),
                    cut_off_count = table.Column<int>(type: "integer", nullable: false),
                    vision_ms = table.Column<int>(type: "integer", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pieces_shown = table.Column<int>(type: "integer", nullable: false),
                    pieces_pre_ticked = table.Column<int>(type: "integer", nullable: false),
                    pieces_ticked = table.Column<int>(type: "integer", nullable: false),
                    ticked_pre_ticked = table.Column<int>(type: "integer", nullable: false),
                    category_edits = table.Column<int>(type: "integer", nullable: false),
                    colour_edits = table.Column<int>(type: "integer", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_item_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    objects_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_batches", x => x.id);
                    table.ForeignKey(
                        name: "fk_bulk_batches_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bulk_pieces",
                schema: "wardrobe",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    box_x = table.Column<double>(type: "double precision", nullable: false),
                    box_y = table.Column<double>(type: "double precision", nullable: false),
                    box_w = table.Column<double>(type: "double precision", nullable: false),
                    box_h = table.Column<double>(type: "double precision", nullable: false),
                    pre_ticked = table.Column<bool>(type: "boolean", nullable: false),
                    reasons = table.Column<string[]>(type: "text[]", nullable: false),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    primary_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    secondary_color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    pattern = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    formality = table.Column<short>(type: "smallint", nullable: false),
                    seasons = table.Column<short>(type: "smallint", nullable: false),
                    warnings = table.Column<string[]>(type: "text[]", nullable: false),
                    embedding = table.Column<Vector>(type: "vector(512)", nullable: true),
                    embedding_model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    analysis_json = table.Column<string>(type: "jsonb", nullable: true),
                    cutout_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    thumb_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    similar_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_pieces", x => x.id);
                    table.CheckConstraint("ck_bulk_pieces_formality", "formality BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "fk_bulk_pieces_bulk_batches_batch_id",
                        column: x => x.batch_id,
                        principalSchema: "wardrobe",
                        principalTable: "bulk_batches",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_bulk_pieces_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_bulk_pieces_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_batches_expires_at",
                schema: "wardrobe",
                table: "bulk_batches",
                column: "expires_at",
                filter: "objects_deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_bulk_batches_user_id_created_at",
                schema: "wardrobe",
                table: "bulk_batches",
                columns: new[] { "user_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_bulk_pieces_batch_id_sort_order",
                schema: "wardrobe",
                table: "bulk_pieces",
                columns: new[] { "batch_id", "sort_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bulk_pieces_category_id",
                schema: "wardrobe",
                table: "bulk_pieces",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_bulk_pieces_user_id",
                schema: "wardrobe",
                table: "bulk_pieces",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_pieces",
                schema: "wardrobe");

            migrationBuilder.DropTable(
                name: "bulk_batches",
                schema: "wardrobe");
        }
    }
}
