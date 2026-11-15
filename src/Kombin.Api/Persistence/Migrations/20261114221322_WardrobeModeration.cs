using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WardrobeModeration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "moderated_at",
                schema: "wardrobe",
                table: "items",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "moderation_verdict",
                schema: "wardrobe",
                table: "items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            // Catalog items show the retailer's picture: shareable as before. Photo items wait for wardrobe-cleanup's moderation retry.
            migrationBuilder.Sql("UPDATE wardrobe.items SET moderation_verdict = 'Allow', moderated_at = now() WHERE from_catalog;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "moderated_at",
                schema: "wardrobe",
                table: "items");

            migrationBuilder.DropColumn(
                name: "moderation_verdict",
                schema: "wardrobe",
                table: "items");
        }
    }
}
