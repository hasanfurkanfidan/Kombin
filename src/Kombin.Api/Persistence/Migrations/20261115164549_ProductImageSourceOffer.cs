using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProductImageSourceOffer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "image_source_offer_id",
                schema: "catalog",
                table: "products",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "image_source_offer_id",
                schema: "catalog",
                table: "products");
        }
    }
}
