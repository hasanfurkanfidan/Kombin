using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Social : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "social");

            migrationBuilder.AddColumn<Guid>(
                name: "creator_id",
                schema: "engagement",
                table: "clicks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "post_id",
                schema: "engagement",
                table: "clicks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "blocks",
                schema: "social",
                columns: table => new
                {
                    blocker_id = table.Column<Guid>(type: "uuid", nullable: false),
                    blocked_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_blocks", x => new { x.blocker_id, x.blocked_id });
                    table.CheckConstraint("ck_blocks_not_self", "blocker_id <> blocked_id");
                    table.ForeignKey(
                        name: "fk_blocks_users_blocked_id",
                        column: x => x.blocked_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_blocks_users_blocker_id",
                        column: x => x.blocker_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "creator_profiles",
                schema: "social",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handle = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    bio = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    link = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_creator_profiles", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_creator_profiles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "follows",
                schema: "social",
                columns: table => new
                {
                    follower_id = table.Column<Guid>(type: "uuid", nullable: false),
                    followee_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_follows", x => new { x.follower_id, x.followee_id });
                    table.CheckConstraint("ck_follows_not_self", "follower_id <> followee_id");
                    table.ForeignKey(
                        name: "fk_follows_users_followee_id",
                        column: x => x.followee_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_follows_users_follower_id",
                        column: x => x.follower_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posting_consents",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posting_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_posting_consents_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "posts",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    author_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    caption = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    consent_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    consent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    moderation_verdict = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    moderation_json = table.Column<string>(type: "jsonb", nullable: true),
                    moderation_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    content_type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    image_original_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_feed_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_preview_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_aspect = table.Column<double>(type: "double precision", nullable: true),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    like_count = table.Column<int>(type: "integer", nullable: false),
                    report_count = table.Column<int>(type: "integer", nullable: false),
                    process_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processing_attempts = table.Column<int>(type: "integer", nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    hidden_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_posts", x => x.id);
                    table.CheckConstraint("ck_posts_caption", "char_length(caption) <= 300");
                    table.CheckConstraint("ck_posts_counts", "like_count >= 0 AND report_count >= 0");
                    table.ForeignKey(
                        name: "fk_posts_users_author_id",
                        column: x => x.author_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "referral_events",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    creator_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_tag_id = table.Column<Guid>(type: "uuid", nullable: true),
                    click_id = table.Column<long>(type: "bigint", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    seller_id = table.Column<Guid>(type: "uuid", nullable: true),
                    viewer_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    share_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    order_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    commission_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    creator_amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    currency = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: false),
                    external_order_ref = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    imported_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_referral_events", x => x.id);
                    table.CheckConstraint("ck_referral_events_share", "share_percent BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_referral_events_clicks_click_id",
                        column: x => x.click_id,
                        principalSchema: "engagement",
                        principalTable: "clicks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referral_events_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referral_events_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referral_events_sellers_seller_id",
                        column: x => x.seller_id,
                        principalSchema: "catalog",
                        principalTable: "sellers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referral_events_users_creator_id",
                        column: x => x.creator_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_referral_events_users_viewer_user_id",
                        column: x => x.viewer_user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "likes",
                schema: "social",
                columns: table => new
                {
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_likes", x => new { x.post_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_likes_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_likes_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "post_tags",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    x = table.Column<double>(type: "double precision", nullable: false),
                    y = table.Column<double>(type: "double precision", nullable: false),
                    w = table.Column<double>(type: "double precision", nullable: false),
                    h = table.Column<double>(type: "double precision", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    wardrobe_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    offer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_at_tag = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    label = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_post_tags", x => x.id);
                    table.CheckConstraint("ck_post_tags_box", "x >= 0 AND y >= 0 AND w > 0 AND h > 0 AND x + w <= 1.0001 AND y + h <= 1.0001");
                    table.CheckConstraint("ck_post_tags_kind", "(kind = 'Product' AND product_id IS NOT NULL) OR (kind = 'Wardrobe' AND wardrobe_item_id IS NOT NULL) OR kind = 'Unknown' OR confirmed_at IS NULL");
                    table.ForeignKey(
                        name: "fk_post_tags_categories_category_id",
                        column: x => x.category_id,
                        principalSchema: "catalog",
                        principalTable: "categories",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_post_tags_offers_offer_id",
                        column: x => x.offer_id,
                        principalSchema: "catalog",
                        principalTable: "offers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_post_tags_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_post_tags_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_post_tags_wardrobe_items_wardrobe_item_id",
                        column: x => x.wardrobe_item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "social",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_reports", x => x.id);
                    table.ForeignKey(
                        name: "fk_reports_posts_post_id",
                        column: x => x.post_id,
                        principalSchema: "social",
                        principalTable: "posts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_reports_users_reporter_id",
                        column: x => x.reporter_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_clicks_creator_id_clicked_at",
                schema: "engagement",
                table: "clicks",
                columns: new[] { "creator_id", "clicked_at" },
                filter: "creator_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_blocks_blocked_id",
                schema: "social",
                table: "blocks",
                column: "blocked_id");

            migrationBuilder.CreateIndex(
                name: "ix_creator_profiles_handle",
                schema: "social",
                table: "creator_profiles",
                column: "handle",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_follows_followee_id",
                schema: "social",
                table: "follows",
                column: "followee_id");

            migrationBuilder.CreateIndex(
                name: "ix_likes_user_id",
                schema: "social",
                table: "likes",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_tags_category_id",
                schema: "social",
                table: "post_tags",
                column: "category_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_tags_offer_id",
                schema: "social",
                table: "post_tags",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_post_tags_post_id_position",
                schema: "social",
                table: "post_tags",
                columns: new[] { "post_id", "position" });

            migrationBuilder.CreateIndex(
                name: "ix_post_tags_product_id",
                schema: "social",
                table: "post_tags",
                column: "product_id",
                filter: "product_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_post_tags_wardrobe_item_id",
                schema: "social",
                table: "post_tags",
                column: "wardrobe_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_posting_consents_user_id_version",
                schema: "social",
                table: "posting_consents",
                columns: new[] { "user_id", "version" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_author_id_created_at",
                schema: "social",
                table: "posts",
                columns: new[] { "author_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_posts_author_live",
                schema: "social",
                table: "posts",
                columns: new[] { "author_id", "published_at" },
                descending: new[] { false, true },
                filter: "status = 'Live'");

            migrationBuilder.CreateIndex(
                name: "ix_posts_feed_window",
                schema: "social",
                table: "posts",
                column: "published_at",
                descending: new bool[0],
                filter: "status = 'Live' AND visibility = 'Public'");

            migrationBuilder.CreateIndex(
                name: "ix_posts_status_updated_at",
                schema: "social",
                table: "posts",
                columns: new[] { "status", "updated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_click_id",
                schema: "social",
                table: "referral_events",
                column: "click_id",
                unique: true,
                filter: "kind = 'Click'");

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_creator_id_occurred_at",
                schema: "social",
                table: "referral_events",
                columns: new[] { "creator_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_offer_id",
                schema: "social",
                table: "referral_events",
                column: "offer_id");

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_post_id",
                schema: "social",
                table: "referral_events",
                column: "post_id");

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_product_id",
                schema: "social",
                table: "referral_events",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_seller_id_external_order_ref",
                schema: "social",
                table: "referral_events",
                columns: new[] { "seller_id", "external_order_ref" },
                unique: true,
                filter: "external_order_ref IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_referral_events_viewer_user_id",
                schema: "social",
                table: "referral_events",
                column: "viewer_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_post_id_reporter_id",
                schema: "social",
                table: "reports",
                columns: new[] { "post_id", "reporter_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_reporter_id",
                schema: "social",
                table: "reports",
                column: "reporter_id");

            migrationBuilder.AddForeignKey(
                name: "fk_clicks_users_creator_id",
                schema: "engagement",
                table: "clicks",
                column: "creator_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_clicks_users_creator_id",
                schema: "engagement",
                table: "clicks");

            migrationBuilder.DropTable(
                name: "blocks",
                schema: "social");

            migrationBuilder.DropTable(
                name: "creator_profiles",
                schema: "social");

            migrationBuilder.DropTable(
                name: "follows",
                schema: "social");

            migrationBuilder.DropTable(
                name: "likes",
                schema: "social");

            migrationBuilder.DropTable(
                name: "post_tags",
                schema: "social");

            migrationBuilder.DropTable(
                name: "posting_consents",
                schema: "social");

            migrationBuilder.DropTable(
                name: "referral_events",
                schema: "social");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "social");

            migrationBuilder.DropTable(
                name: "posts",
                schema: "social");

            migrationBuilder.DropIndex(
                name: "ix_clicks_creator_id_clicked_at",
                schema: "engagement",
                table: "clicks");

            migrationBuilder.DropColumn(
                name: "creator_id",
                schema: "engagement",
                table: "clicks");

            migrationBuilder.DropColumn(
                name: "post_id",
                schema: "engagement",
                table: "clicks");
        }
    }
}
