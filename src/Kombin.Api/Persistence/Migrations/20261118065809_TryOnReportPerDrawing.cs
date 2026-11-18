using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TryOnReportPerDrawing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_reports_user_id_outfit_id_product_id_renderer",
                schema: "tryon",
                table: "reports");

            migrationBuilder.AddColumn<Guid>(
                name: "render_id",
                schema: "tryon",
                table: "reports",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_reports_render_id",
                schema: "tryon",
                table: "reports",
                column: "render_id");

            migrationBuilder.CreateIndex(
                name: "ix_reports_user_id_render_id",
                schema: "tryon",
                table: "reports",
                columns: new[] { "user_id", "render_id" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_reports_renders_render_id",
                schema: "tryon",
                table: "reports",
                column: "render_id",
                principalSchema: "tryon",
                principalTable: "renders",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_reports_renders_render_id",
                schema: "tryon",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "ix_reports_render_id",
                schema: "tryon",
                table: "reports");

            migrationBuilder.DropIndex(
                name: "ix_reports_user_id_render_id",
                schema: "tryon",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "render_id",
                schema: "tryon",
                table: "reports");

            migrationBuilder.CreateIndex(
                name: "ix_reports_user_id_outfit_id_product_id_renderer",
                schema: "tryon",
                table: "reports",
                columns: new[] { "user_id", "outfit_id", "product_id", "renderer" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
