using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaColorsBlockStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccentColor",
                schema: "site",
                table: "Settings",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "BackgroundColor",
                schema: "site",
                table: "Settings",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "TextColor",
                schema: "site",
                table: "Settings",
                type: "nvarchar(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PhotoId",
                schema: "site",
                table: "CatalogDoctors",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Style",
                schema: "site",
                table: "Blocks",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                schema: "site",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    FileName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Size = table.Column<int>(type: "int", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Alt = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Data = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    UploadedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogDoctors_PhotoId",
                schema: "site",
                table: "CatalogDoctors",
                column: "PhotoId");

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Sha256",
                schema: "site",
                table: "MediaFiles",
                column: "Sha256");

            migrationBuilder.AddForeignKey(
                name: "FK_CatalogDoctors_MediaFiles_PhotoId",
                schema: "site",
                table: "CatalogDoctors",
                column: "PhotoId",
                principalSchema: "site",
                principalTable: "MediaFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CatalogDoctors_MediaFiles_PhotoId",
                schema: "site",
                table: "CatalogDoctors");

            migrationBuilder.DropTable(
                name: "MediaFiles",
                schema: "site");

            migrationBuilder.DropIndex(
                name: "IX_CatalogDoctors_PhotoId",
                schema: "site",
                table: "CatalogDoctors");

            migrationBuilder.DropColumn(
                name: "AccentColor",
                schema: "site",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "BackgroundColor",
                schema: "site",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "TextColor",
                schema: "site",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PhotoId",
                schema: "site",
                table: "CatalogDoctors");

            migrationBuilder.DropColumn(
                name: "Style",
                schema: "site",
                table: "Blocks");
        }
    }
}
