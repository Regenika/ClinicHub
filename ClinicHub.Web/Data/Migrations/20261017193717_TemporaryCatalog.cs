using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TemporaryCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogDoctors",
                schema: "site",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogDoctors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogServices",
                schema: "site",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Key = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Price = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    DurationMinutes = table.Column<int>(type: "int", nullable: false),
                    IsVisible = table.Column<bool>(type: "bit", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogServices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogDoctorServices",
                schema: "site",
                columns: table => new
                {
                    DoctorId = table.Column<int>(type: "int", nullable: false),
                    ServiceId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogDoctorServices", x => new { x.DoctorId, x.ServiceId });
                    table.ForeignKey(
                        name: "FK_CatalogDoctorServices_CatalogDoctors_DoctorId",
                        column: x => x.DoctorId,
                        principalSchema: "site",
                        principalTable: "CatalogDoctors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogDoctorServices_CatalogServices_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "site",
                        principalTable: "CatalogServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogDoctors_Key",
                schema: "site",
                table: "CatalogDoctors",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogDoctorServices_ServiceId",
                schema: "site",
                table: "CatalogDoctorServices",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogServices_Key",
                schema: "site",
                table: "CatalogServices",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogDoctorServices",
                schema: "site");

            migrationBuilder.DropTable(
                name: "CatalogDoctors",
                schema: "site");

            migrationBuilder.DropTable(
                name: "CatalogServices",
                schema: "site");
        }
    }
}
