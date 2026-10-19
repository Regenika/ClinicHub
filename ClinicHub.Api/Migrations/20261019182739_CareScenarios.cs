using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClinicHub.Api.Migrations
{
    /// <inheritdoc />
    public partial class CareScenarios : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CareScenarios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Trigger = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    NoVisitDays = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareScenarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CareScenarioService",
                columns: table => new
                {
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    ServiceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareScenarioService", x => new { x.ScenarioId, x.ServiceId });
                    table.ForeignKey(
                        name: "FK_CareScenarioService_CareScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "CareScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CareScenarioService_Services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "catalog",
                        principalTable: "Services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CareScenarioSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ScenarioId = table.Column<int>(type: "int", nullable: false),
                    Order = table.Column<int>(type: "int", nullable: false),
                    OffsetDays = table.Column<int>(type: "int", nullable: false),
                    SendTime = table.Column<TimeSpan>(type: "time", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CancelIfFutureVisit = table.Column<bool>(type: "bit", nullable: false),
                    CancelIfVisitAfter = table.Column<bool>(type: "bit", nullable: false),
                    CancelIfServiceAfterId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CareScenarioSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CareScenarioSteps_CareScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "CareScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlannedReminders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PatientId = table.Column<int>(type: "int", nullable: false),
                    ScenarioId = table.Column<int>(type: "int", nullable: true),
                    StepId = table.Column<int>(type: "int", nullable: true),
                    TriggerVisitId = table.Column<int>(type: "int", nullable: true),
                    EventAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Kind = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Purpose = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    DueAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    StatusReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    CreatedById = table.Column<int>(type: "int", nullable: true),
                    RequestId = table.Column<int>(type: "int", nullable: true),
                    SentAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlannedReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlannedReminders_CareScenarioSteps_StepId",
                        column: x => x.StepId,
                        principalTable: "CareScenarioSteps",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PlannedReminders_CareScenarios_ScenarioId",
                        column: x => x.ScenarioId,
                        principalTable: "CareScenarios",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_PlannedReminders_Patients_PatientId",
                        column: x => x.PatientId,
                        principalTable: "Patients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CareScenarioService_ServiceId",
                table: "CareScenarioService",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CareScenarioSteps_ScenarioId",
                table: "CareScenarioSteps",
                column: "ScenarioId");

            migrationBuilder.CreateIndex(
                name: "IX_PlannedReminders_PatientId_Status",
                table: "PlannedReminders",
                columns: new[] { "PatientId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedReminders_ScenarioId_PatientId",
                table: "PlannedReminders",
                columns: new[] { "ScenarioId", "PatientId" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedReminders_Status_DueAt",
                table: "PlannedReminders",
                columns: new[] { "Status", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PlannedReminders_StepId",
                table: "PlannedReminders",
                column: "StepId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CareScenarioService");

            migrationBuilder.DropTable(
                name: "PlannedReminders");

            migrationBuilder.DropTable(
                name: "CareScenarioSteps");

            migrationBuilder.DropTable(
                name: "CareScenarios");
        }
    }
}
