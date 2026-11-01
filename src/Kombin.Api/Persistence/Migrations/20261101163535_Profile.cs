using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Profile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "profile");

            migrationBuilder.CreateTable(
                name: "personas",
                schema: "profile",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    icon = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    budget_min = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    budget_max = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    formality_min = table.Column<short>(type: "smallint", nullable: false),
                    formality_max = table.Column<short>(type: "smallint", nullable: false),
                    preferred_colors = table.Column<string[]>(type: "text[]", nullable: false),
                    avoided_colors = table.Column<string[]>(type: "text[]", nullable: false),
                    preferred_fits = table.Column<string[]>(type: "text[]", nullable: false),
                    seasons = table.Column<short>(type: "smallint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_personas", x => x.id);
                    table.CheckConstraint("ck_personas_budget", "budget_min IS NULL OR budget_max IS NULL OR budget_min <= budget_max");
                    table.CheckConstraint("ck_personas_formality", "formality_min BETWEEN 1 AND 5 AND formality_max BETWEEN 1 AND 5 AND formality_min <= formality_max");
                    table.ForeignKey(
                        name: "fk_personas_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "size_entries",
                schema: "profile",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    brand_id = table.Column<Guid>(type: "uuid", nullable: false),
                    group = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    size = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    confidence = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_size_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_size_entries_brands_brand_id",
                        column: x => x.brand_id,
                        principalSchema: "catalog",
                        principalTable: "brands",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_size_entries_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "style_cards",
                schema: "profile",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    title = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    image_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    family = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    colors = table.Column<string[]>(type: "text[]", nullable: false),
                    formality = table.Column<short>(type: "smallint", nullable: false),
                    fits = table.Column<string[]>(type: "text[]", nullable: false),
                    seasons = table.Column<short>(type: "smallint", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_style_cards", x => x.id);
                    table.CheckConstraint("ck_style_cards_formality", "formality BETWEEN 1 AND 5");
                });

            migrationBuilder.CreateTable(
                name: "persona_style_profiles",
                schema: "profile",
                columns: table => new
                {
                    persona_id = table.Column<Guid>(type: "uuid", nullable: false),
                    color_weights = table.Column<Dictionary<string, double>>(type: "jsonb", nullable: false),
                    family_weights = table.Column<Dictionary<string, double>>(type: "jsonb", nullable: false),
                    fit_weights = table.Column<Dictionary<string, double>>(type: "jsonb", nullable: false),
                    formality_mean = table.Column<decimal>(type: "numeric(3,2)", precision: 3, scale: 2, nullable: false),
                    season_weights = table.Column<Dictionary<string, double>>(type: "jsonb", nullable: false),
                    like_count = table.Column<int>(type: "integer", nullable: false),
                    dislike_count = table.Column<int>(type: "integer", nullable: false),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_persona_style_profiles", x => x.persona_id);
                    table.ForeignKey(
                        name: "fk_persona_style_profiles_personas_persona_id",
                        column: x => x.persona_id,
                        principalSchema: "profile",
                        principalTable: "personas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_profiles",
                schema: "profile",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audience = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    birth_year = table.Column<int>(type: "integer", nullable: true),
                    height_cm = table.Column<int>(type: "integer", nullable: true),
                    weight_kg = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    chest_cm = table.Column<int>(type: "integer", nullable: true),
                    waist_cm = table.Column<int>(type: "integer", nullable: true),
                    hip_cm = table.Column<int>(type: "integer", nullable: true),
                    shoe_size_eu = table.Column<decimal>(type: "numeric(4,1)", precision: 4, scale: 1, nullable: true),
                    active_persona_id = table.Column<Guid>(type: "uuid", nullable: true),
                    onboarding_completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_profiles", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_user_profiles_personas_active_persona_id",
                        column: x => x.active_persona_id,
                        principalSchema: "profile",
                        principalTable: "personas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_user_profiles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "style_swipes",
                schema: "profile",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    persona_id = table.Column<Guid>(type: "uuid", nullable: false),
                    style_card_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_style_swipes", x => x.id);
                    table.ForeignKey(
                        name: "fk_style_swipes_personas_persona_id",
                        column: x => x.persona_id,
                        principalSchema: "profile",
                        principalTable: "personas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_style_swipes_style_cards_style_card_id",
                        column: x => x.style_card_id,
                        principalSchema: "profile",
                        principalTable: "style_cards",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_personas_user_id_name",
                schema: "profile",
                table: "personas",
                columns: new[] { "user_id", "name" },
                unique: true,
                filter: "NOT is_archived");

            migrationBuilder.CreateIndex(
                name: "ix_personas_user_id_sort_order",
                schema: "profile",
                table: "personas",
                columns: new[] { "user_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_size_entries_brand_id",
                schema: "profile",
                table: "size_entries",
                column: "brand_id");

            migrationBuilder.CreateIndex(
                name: "ix_size_entries_user_id_brand_id_group",
                schema: "profile",
                table: "size_entries",
                columns: new[] { "user_id", "brand_id", "group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_style_cards_is_active_sort_order",
                schema: "profile",
                table: "style_cards",
                columns: new[] { "is_active", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ix_style_swipes_persona_id_style_card_id",
                schema: "profile",
                table: "style_swipes",
                columns: new[] { "persona_id", "style_card_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_style_swipes_style_card_id",
                schema: "profile",
                table: "style_swipes",
                column: "style_card_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_profiles_active_persona_id",
                schema: "profile",
                table: "user_profiles",
                column: "active_persona_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "persona_style_profiles",
                schema: "profile");

            migrationBuilder.DropTable(
                name: "size_entries",
                schema: "profile");

            migrationBuilder.DropTable(
                name: "style_swipes",
                schema: "profile");

            migrationBuilder.DropTable(
                name: "user_profiles",
                schema: "profile");

            migrationBuilder.DropTable(
                name: "style_cards",
                schema: "profile");

            migrationBuilder.DropTable(
                name: "personas",
                schema: "profile");
        }
    }
}
