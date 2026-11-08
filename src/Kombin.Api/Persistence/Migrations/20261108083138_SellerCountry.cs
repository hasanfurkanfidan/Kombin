using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kombin.Api.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SellerCountry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "country",
                schema: "catalog",
                table: "sellers",
                type: "char(2)",
                nullable: false,
                defaultValue: "TR");

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "sellers",
                keyColumn: "id",
                keyValue: new Guid("5e110000-0000-7000-8000-000000000001"),
                column: "country",
                value: "TR");

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "sellers",
                keyColumn: "id",
                keyValue: new Guid("5e110000-0000-7000-8000-000000000002"),
                column: "country",
                value: "TR");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "country",
                schema: "catalog",
                table: "sellers");
        }
    }
}
