using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class MediaFraming : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FocusX",
                schema: "site",
                table: "MediaFiles",
                type: "int",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "FocusY",
                schema: "site",
                table: "MediaFiles",
                type: "int",
                nullable: false,
                defaultValue: 50);

            migrationBuilder.AddColumn<int>(
                name: "Zoom",
                schema: "site",
                table: "MediaFiles",
                type: "int",
                nullable: false,
                defaultValue: 100);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FocusX",
                schema: "site",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "FocusY",
                schema: "site",
                table: "MediaFiles");

            migrationBuilder.DropColumn(
                name: "Zoom",
                schema: "site",
                table: "MediaFiles");
        }
    }
}
