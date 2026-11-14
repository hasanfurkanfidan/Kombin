using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_referral_events_users_creator_id",
                schema: "social",
                table: "referral_events");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deletion_requested_at",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "creator_id",
                schema: "social",
                table: "referral_events",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "status_before_deletion",
                schema: "social",
                table: "posts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_deletion_requested_at",
                schema: "identity",
                table: "users",
                column: "deletion_requested_at",
                filter: "deletion_requested_at IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "fk_referral_events_users_creator_id",
                schema: "social",
                table: "referral_events",
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
                name: "fk_referral_events_users_creator_id",
                schema: "social",
                table: "referral_events");

            migrationBuilder.DropIndex(
                name: "ix_users_deletion_requested_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "deletion_requested_at",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "status_before_deletion",
                schema: "social",
                table: "posts");

            migrationBuilder.AlterColumn<Guid>(
                name: "creator_id",
                schema: "social",
                table: "referral_events",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "fk_referral_events_users_creator_id",
                schema: "social",
                table: "referral_events",
                column: "creator_id",
                principalSchema: "identity",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
