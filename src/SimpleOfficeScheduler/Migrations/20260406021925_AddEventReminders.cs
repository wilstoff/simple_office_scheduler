using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleOfficeScheduler.Migrations
{
    /// <inheritdoc />
    public partial class AddEventReminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EventReminderDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EventReminderDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EventReminderDefinitions_Events_EventId",
                        column: x => x.EventId,
                        principalTable: "Events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OccurrenceReminderValues",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventOccurrenceId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReminderDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OccurrenceReminderValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OccurrenceReminderValues_EventOccurrences_EventOccurrenceId",
                        column: x => x.EventOccurrenceId,
                        principalTable: "EventOccurrences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OccurrenceReminderValues_EventReminderDefinitions_ReminderDefinitionId",
                        column: x => x.ReminderDefinitionId,
                        principalTable: "EventReminderDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EventReminderDefinitions_EventId_Name",
                table: "EventReminderDefinitions",
                columns: new[] { "EventId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OccurrenceReminderValues_EventOccurrenceId_ReminderDefinitionId",
                table: "OccurrenceReminderValues",
                columns: new[] { "EventOccurrenceId", "ReminderDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OccurrenceReminderValues_ReminderDefinitionId",
                table: "OccurrenceReminderValues",
                column: "ReminderDefinitionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OccurrenceReminderValues");

            migrationBuilder.DropTable(
                name: "EventReminderDefinitions");
        }
    }
}
