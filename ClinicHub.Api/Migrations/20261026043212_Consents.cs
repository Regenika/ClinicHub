using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class Consents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "consents");

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "SmsRequests",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Marketing");

            migrationBuilder.AddColumn<string>(
                name: "SkipReason",
                table: "RequestRecipients",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Purpose",
                table: "AutoRules",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Marketing");

            migrationBuilder.CreateTable(
                name: "Records",
                schema: "consents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Channel = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Granted = table.Column<bool>(type: "bit", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    EvidenceRef = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ById = table.Column<int>(type: "int", nullable: true),
                    Comment = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Records_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Records_Users_ById",
                        column: x => x.ById,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Records_ById",
                schema: "consents",
                table: "Records",
                column: "ById");

            migrationBuilder.CreateIndex(
                name: "IX_Records_PatientId_Purpose_At",
                schema: "consents",
                table: "Records",
                columns: new[] { "PatientId", "Purpose", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Records",
                schema: "consents");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "SmsRequests");

            migrationBuilder.DropColumn(
                name: "SkipReason",
                table: "RequestRecipients");

            migrationBuilder.DropColumn(
                name: "Purpose",
                table: "AutoRules");
        }
    }
}
