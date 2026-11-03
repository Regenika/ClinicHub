using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminEditorsAndPriceHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CatalogPriceChanges",
                schema: "site",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ServiceId = table.Column<int>(type: "int", nullable: false),
                    OldPrice = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    NewPrice = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ChangedBy = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogPriceChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogPriceChanges_CatalogServices_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "site",
                        principalTable: "CatalogServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Editors",
                schema: "site",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Login = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Editors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogPriceChanges_ServiceId_ChangedAt",
                schema: "site",
                table: "CatalogPriceChanges",
                columns: new[] { "ServiceId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Editors_Login",
                schema: "site",
                table: "Editors",
                column: "Login",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CatalogPriceChanges",
                schema: "site");

            migrationBuilder.DropTable(
                name: "Editors",
                schema: "site");
        }
    }
}
