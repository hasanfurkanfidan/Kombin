using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class League : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "league");

            migrationBuilder.CreateTable(
                name: "consents",
                schema: "league",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_consents", x => x.id);
                    table.ForeignKey(
                        name: "fk_consents_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "days",
                schema: "league",
                columns: table => new
                {
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    closes_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ranked_count = table.Column<int>(type: "integer", nullable: false),
                    engine_count = table.Column<int>(type: "integer", nullable: false),
                    pair_count = table.Column<int>(type: "integer", nullable: false),
                    bt_iterations = table.Column<int>(type: "integer", nullable: false),
                    bt_converged = table.Column<bool>(type: "boolean", nullable: false),
                    close_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_days", x => x.day);
                });

            migrationBuilder.CreateTable(
                name: "engine_preferences",
                schema: "league",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occasion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    winner_products = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    loser_products = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    weight = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_engine_preferences", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entries",
                schema: "league",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    outfit_id = table.Column<Guid>(type: "uuid", nullable: true),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occasion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    piece_ids = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    piece_set_hash = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    exposures = table.Column<int>(type: "integer", nullable: false),
                    comparisons = table.Column<int>(type: "integer", nullable: false),
                    weighted_wins = table.Column<double>(type: "double precision", nullable: false),
                    weighted_games = table.Column<double>(type: "double precision", nullable: false),
                    counting_reports = table.Column<int>(type: "integer", nullable: false),
                    locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    strength = table.Column<double>(type: "double precision", nullable: true),
                    percentile = table.Column<double>(type: "double precision", nullable: true),
                    rank = table.Column<int>(type: "integer", nullable: true),
                    reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entries", x => x.id);
                    table.CheckConstraint("ck_entries_counters", "exposures >= 0 AND comparisons >= 0 AND counting_reports >= 0 AND weighted_wins >= 0 AND weighted_wins <= weighted_games");
                    table.CheckConstraint("ck_entries_owner", "(kind = 'Human') = (user_id IS NOT NULL)");
                    table.CheckConstraint("ck_entries_percentile", "percentile IS NULL OR percentile BETWEEN 0 AND 1");
                    table.CheckConstraint("ck_entries_reason", "(status = 'NotCompeting') = (reason IS NOT NULL)");
                    table.ForeignKey(
                        name: "fk_entries_outfits_outfit_id",
                        column: x => x.outfit_id,
                        principalSchema: "recommendation",
                        principalTable: "outfits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_entries_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "players",
                schema: "league",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    left_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consent_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    counted_votes = table.Column<int>(type: "integer", nullable: false),
                    idle_weeks = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_players", x => x.user_id);
                    table.CheckConstraint("ck_players_counters", "counted_votes >= 0 AND idle_weeks >= 0");
                    table.ForeignKey(
                        name: "fk_players_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "state",
                schema: "league",
                columns: table => new
                {
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_state", x => x.key);
                });

            migrationBuilder.CreateTable(
                name: "style_scores",
                schema: "league",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    computed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    counted_days = table.Column<int>(type: "integer", nullable: false),
                    mean_p = table.Column<double>(type: "double precision", nullable: true),
                    sd_p = table.Column<double>(type: "double precision", nullable: true),
                    score = table.Column<double>(type: "double precision", nullable: true),
                    qualifying_weeks = table.Column<short>(type: "smallint", nullable: false),
                    league_eligible_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_style_scores", x => x.user_id);
                    table.CheckConstraint("ck_style_scores_score", "score IS NULL OR score BETWEEN 0 AND 100");
                    table.ForeignKey(
                        name: "fk_style_scores_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "weeks",
                schema: "league",
                columns: table => new
                {
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    ends_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    groups_formed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_weeks", x => x.week_start);
                    table.CheckConstraint("ck_weeks_monday", "extract(isodow FROM week_start) = 1");
                });

            migrationBuilder.CreateTable(
                name: "daily_scores",
                schema: "league",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    entry_id = table.Column<Guid>(type: "uuid", nullable: true),
                    competed = table.Column<bool>(type: "boolean", nullable: false),
                    ranked = table.Column<bool>(type: "boolean", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: true),
                    ranked_count = table.Column<int>(type: "integer", nullable: false),
                    comparisons = table.Column<int>(type: "integer", nullable: false),
                    percentile = table.Column<double>(type: "double precision", nullable: true),
                    vote_points = table.Column<int>(type: "integer", nullable: false),
                    voting_points = table.Column<int>(type: "integer", nullable: false),
                    worn_points = table.Column<int>(type: "integer", nullable: false),
                    piece_points = table.Column<int>(type: "integer", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_daily_scores", x => new { x.user_id, x.day });
                    table.CheckConstraint("ck_daily_scores_parts", "vote_points BETWEEN 0 AND 70 AND voting_points IN (0, 15) AND worn_points IN (0, 10) AND piece_points IN (0, 5)");
                    table.CheckConstraint("ck_daily_scores_total", "total = vote_points + voting_points + worn_points + piece_points");
                    table.ForeignKey(
                        name: "fk_daily_scores_league_entries_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "league",
                        principalTable: "entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_daily_scores_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "entry_pieces",
                schema: "league",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    position = table.Column<short>(type: "smallint", nullable: false),
                    slot = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    wardrobe_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    product_id = table.Column<Guid>(type: "uuid", nullable: true),
                    image_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entry_pieces", x => new { x.entry_id, x.position });
                    table.CheckConstraint("ck_entry_pieces_image", "image_key IS NULL OR image_url IS NULL");
                    table.ForeignKey(
                        name: "fk_entry_pieces_entries_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "league",
                        principalTable: "entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_entry_pieces_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "catalog",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_entry_pieces_wardrobe_items_wardrobe_item_id",
                        column: x => x.wardrobe_item_id,
                        principalSchema: "wardrobe",
                        principalTable: "items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "pairs",
                schema: "league",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    voter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    left_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    right_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    low_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    high_entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    match_level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    sequence = table.Column<short>(type: "smallint", nullable: false),
                    served_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    answered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    choice = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    client_decision_ms = table.Column<int>(type: "integer", nullable: true),
                    counted = table.Column<bool>(type: "boolean", nullable: false),
                    weight = table.Column<float>(type: "real", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pairs", x => x.id);
                    table.CheckConstraint("ck_pairs_answer", "(answered_at IS NULL) = (choice IS NULL)");
                    table.CheckConstraint("ck_pairs_sequence", "sequence >= 1");
                    table.CheckConstraint("ck_pairs_two_entries", "left_entry_id <> right_entry_id AND low_entry_id < high_entry_id");
                    table.CheckConstraint("ck_pairs_weight", "weight >= 0 AND weight <= 1 AND (counted OR weight = 0)");
                    table.ForeignKey(
                        name: "fk_pairs_entries_left_entry_id",
                        column: x => x.left_entry_id,
                        principalSchema: "league",
                        principalTable: "entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pairs_entries_right_entry_id",
                        column: x => x.right_entry_id,
                        principalSchema: "league",
                        principalTable: "entries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_pairs_users_voter_id",
                        column: x => x.voter_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reports",
                schema: "league",
                columns: table => new
                {
                    entry_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reporter_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    counts = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_league_reports", x => new { x.entry_id, x.reporter_id });
                    table.ForeignKey(
                        name: "fk_reports_entries_entry_id",
                        column: x => x.entry_id,
                        principalSchema: "league",
                        principalTable: "entries",
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

            migrationBuilder.CreateTable(
                name: "groups",
                schema: "league",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    tiers = table.Column<string[]>(type: "text[]", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_groups", x => x.id);
                    table.ForeignKey(
                        name: "fk_groups_weeks_week_start",
                        column: x => x.week_start,
                        principalSchema: "league",
                        principalTable: "weeks",
                        principalColumn: "week_start",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "group_members",
                schema: "league",
                columns: table => new
                {
                    week_start = table.Column<DateOnly>(type: "date", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    alias = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    tier = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    week_points = table.Column<int>(type: "integer", nullable: true),
                    competed_days = table.Column<short>(type: "smallint", nullable: true),
                    final_rank = table.Column<int>(type: "integer", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_group_members", x => new { x.week_start, x.user_id });
                    table.ForeignKey(
                        name: "fk_group_members_groups_group_id",
                        column: x => x.group_id,
                        principalSchema: "league",
                        principalTable: "groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_group_members_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_consents_user_id_at",
                schema: "league",
                table: "consents",
                columns: new[] { "user_id", "at" });

            migrationBuilder.CreateIndex(
                name: "ix_daily_scores_day",
                schema: "league",
                table: "daily_scores",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_daily_scores_entry_id",
                schema: "league",
                table: "daily_scores",
                column: "entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_engine_preferences_day",
                schema: "league",
                table: "engine_preferences",
                column: "day");

            migrationBuilder.CreateIndex(
                name: "ix_entries_day_status_audience_occasion_exposures",
                schema: "league",
                table: "entries",
                columns: new[] { "day", "status", "audience", "occasion", "exposures" });

            migrationBuilder.CreateIndex(
                name: "ix_entries_day_user_id",
                schema: "league",
                table: "entries",
                columns: new[] { "day", "user_id" },
                unique: true,
                filter: "user_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entries_outfit_id",
                schema: "league",
                table: "entries",
                column: "outfit_id");

            migrationBuilder.CreateIndex(
                name: "ix_entries_user_id_day",
                schema: "league",
                table: "entries",
                columns: new[] { "user_id", "day" });

            migrationBuilder.CreateIndex(
                name: "ix_entry_pieces_image_key",
                schema: "league",
                table: "entry_pieces",
                column: "image_key",
                filter: "image_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_entry_pieces_product_id",
                schema: "league",
                table: "entry_pieces",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ix_entry_pieces_wardrobe_item_id",
                schema: "league",
                table: "entry_pieces",
                column: "wardrobe_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_group_members_group_id_alias",
                schema: "league",
                table: "group_members",
                columns: new[] { "group_id", "alias" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_members_user_id_week_start",
                schema: "league",
                table: "group_members",
                columns: new[] { "user_id", "week_start" });

            migrationBuilder.CreateIndex(
                name: "ix_groups_week_start",
                schema: "league",
                table: "groups",
                column: "week_start");

            migrationBuilder.CreateIndex(
                name: "ix_pairs_day",
                schema: "league",
                table: "pairs",
                column: "day")
                .Annotation("Npgsql:IndexInclude", new[] { "left_entry_id", "right_entry_id", "choice", "weight" });

            migrationBuilder.CreateIndex(
                name: "ix_pairs_left_entry_id",
                schema: "league",
                table: "pairs",
                column: "left_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_pairs_open",
                schema: "league",
                table: "pairs",
                columns: new[] { "voter_id", "day" },
                unique: true,
                filter: "answered_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_pairs_right_entry_id",
                schema: "league",
                table: "pairs",
                column: "right_entry_id");

            migrationBuilder.CreateIndex(
                name: "ix_pairs_voter_id_day_low_entry_id_high_entry_id",
                schema: "league",
                table: "pairs",
                columns: new[] { "voter_id", "day", "low_entry_id", "high_entry_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pairs_voter_id_day_sequence",
                schema: "league",
                table: "pairs",
                columns: new[] { "voter_id", "day", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_league_reports_reporter_id",
                schema: "league",
                table: "reports",
                column: "reporter_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "consents",
                schema: "league");

            migrationBuilder.DropTable(
                name: "daily_scores",
                schema: "league");

            migrationBuilder.DropTable(
                name: "days",
                schema: "league");

            migrationBuilder.DropTable(
                name: "engine_preferences",
                schema: "league");

            migrationBuilder.DropTable(
                name: "entry_pieces",
                schema: "league");

            migrationBuilder.DropTable(
                name: "group_members",
                schema: "league");

            migrationBuilder.DropTable(
                name: "pairs",
                schema: "league");

            migrationBuilder.DropTable(
                name: "players",
                schema: "league");

            migrationBuilder.DropTable(
                name: "reports",
                schema: "league");

            migrationBuilder.DropTable(
                name: "state",
                schema: "league");

            migrationBuilder.DropTable(
                name: "style_scores",
                schema: "league");

            migrationBuilder.DropTable(
                name: "groups",
                schema: "league");

            migrationBuilder.DropTable(
                name: "entries",
                schema: "league");

            migrationBuilder.DropTable(
                name: "weeks",
                schema: "league");
        }
    }
}
