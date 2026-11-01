using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Notification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notification");

            migrationBuilder.CreateTable(
                name: "devices",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expo_push_token = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    platform = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    app_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    device_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    deactivated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    deactivation_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_devices", x => x.id);
                    table.ForeignKey(
                        name: "fk_devices_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_preferences",
                schema: "notification",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    stock_alerts = table.Column<bool>(type: "boolean", nullable: false),
                    recommendations = table.Column<bool>(type: "boolean", nullable: false),
                    max_recommendations_per_week = table.Column<int>(type: "integer", nullable: false),
                    quiet_hours_start = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    quiet_hours_end = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_preferences", x => x.user_id);
                    table.ForeignKey(
                        name: "fk_notification_preferences_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notification",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    body = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    read_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    failure_reason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    expo_ticket_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    receipt_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    alert_trigger_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_notifications_alert_triggers_alert_trigger_id",
                        column: x => x.alert_trigger_id,
                        principalSchema: "engagement",
                        principalTable: "alert_triggers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_notifications_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_alert_triggers_unconsumed",
                schema: "engagement",
                table: "alert_triggers",
                column: "triggered_at",
                filter: "consumed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_devices_expo_push_token",
                schema: "notification",
                table: "devices",
                column: "expo_push_token",
                unique: true,
                filter: "is_active");

            migrationBuilder.CreateIndex(
                name: "ix_devices_user_id_is_active",
                schema: "notification",
                table: "devices",
                columns: new[] { "user_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_alert_trigger_id",
                schema: "notification",
                table: "notifications",
                column: "alert_trigger_id");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_status_scheduled_for",
                schema: "notification",
                table: "notifications",
                columns: new[] { "status", "scheduled_for" });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_created_at",
                schema: "notification",
                table: "notifications",
                columns: new[] { "user_id", "created_at" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "ix_notifications_user_id_dedupe_key_created_at",
                schema: "notification",
                table: "notifications",
                columns: new[] { "user_id", "dedupe_key", "created_at" },
                descending: new[] { false, false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "devices",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "notification_preferences",
                schema: "notification");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notification");

            migrationBuilder.DropIndex(
                name: "ix_alert_triggers_unconsumed",
                schema: "engagement",
                table: "alert_triggers");
        }
    }
}
