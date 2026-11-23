using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AskFriend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "askfriend");

            migrationBuilder.CreateTable(
                name: "asks",
                schema: "askfriend",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(22)", maxLength: 22, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    consent_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    copies_deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    page_views = table.Column<int>(type: "integer", nullable: false),
                    store_clicks = table.Column<int>(type: "integer", nullable: false),
                    push_count = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_asks", x => x.id);
                    table.CheckConstraint("ck_asks_closed", "(status = 'Closed') = (closed_at IS NOT NULL)");
                    table.CheckConstraint("ck_asks_counters", "page_views >= 0 AND store_clicks >= 0 AND push_count >= 0");
                    table.ForeignKey(
                        name: "fk_asks_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "askfriend",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_askfriend_consents", x => new { x.user_id, x.version });
                    table.ForeignKey(
                        name: "fk_consents_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "answers",
                schema: "askfriend",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ask_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voter_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    choice = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    note = table.Column<string>(type: "character varying(140)", maxLength: 140, nullable: true),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hidden_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_answers", x => x.id);
                    table.ForeignKey(
                        name: "fk_answers_asks_ask_id",
                        column: x => x.ask_id,
                        principalSchema: "askfriend",
                        principalTable: "asks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "pieces",
                schema: "askfriend",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ask_id = table.Column<Guid>(type: "uuid", nullable: false),
                    option = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    slot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    wardrobe_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    copy_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pieces", x => x.id);
                    table.CheckConstraint("ck_pieces_image", "copy_key IS NULL OR image_url IS NULL");
                    table.ForeignKey(
                        name: "fk_pieces_asks_ask_id",
                        column: x => x.ask_id,
                        principalSchema: "askfriend",
                        principalTable: "asks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pieces_outfits_outfit_id",
                        column: x => x.outfit_id,
                        principalSchema: "recommendation",
                        principalTable: "outfits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pieces_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_pieces_wardrobe_items_wardrobe_item_id",
                        column: x => x.wardrobe_item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "askfriend",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    ask_id = table.Column<Guid>(type: "uuid", nullable: false),
                    voter_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_askfriend_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_reports_asks_ask_id",
                        column: x => x.ask_id,
                        principalSchema: "askfriend",
                        principalTable: "asks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_answers_ask_id_voter_hash",
                schema: "askfriend",
                table: "answers",
                columns: new[] { "ask_id", "voter_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_answers_notified_at",
                schema: "askfriend",
                table: "answers",
                column: "notified_at",
                filter: "notified_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asks_expires_at",
                schema: "askfriend",
                table: "asks",
                column: "expires_at",
                filter: "copies_deleted_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_asks_token",
                schema: "askfriend",
                table: "asks",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_asks_user_id_created_at",
                schema: "askfriend",
                table: "asks",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_pieces_ask_id_option_position",
                schema: "askfriend",
                table: "pieces",
                columns: new[] { "ask_id", "option", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pieces_outfit_id",
                schema: "askfriend",
                table: "pieces",
                column: "outfit_id");

            migrationBuilder.CreateIndex(
                name: "ix_pieces_product_id",
                schema: "askfriend",
                table: "pieces",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_pieces_wardrobe_item_id",
                schema: "askfriend",
                table: "pieces",
                column: "wardrobe_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_ask_id_voter_hash",
                schema: "askfriend",
                table: "reports",
                columns: new[] { "ask_id", "voter_hash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answers",
                schema: "askfriend");

            migrationBuilder.DropTable(
                name: "consents",
                schema: "askfriend");

            migrationBuilder.DropTable(
                name: "pieces",
                schema: "askfriend");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "askfriend");

            migrationBuilder.DropTable(
                name: "asks",
                schema: "askfriend");
        }
    }
}
