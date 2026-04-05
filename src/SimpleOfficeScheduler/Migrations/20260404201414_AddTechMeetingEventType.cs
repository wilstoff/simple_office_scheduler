using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleOfficeScheduler.Migrations
{
    /// <inheritdoc />
    public partial class AddTechMeetingEventType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventType",
                table: "Events",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "IsLightningRound",
                table: "EventOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NamePrefix",
                table: "EventOccurrences",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameSuffix",
                table: "EventOccurrences",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OccurrenceContributors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventOccurrenceId = table.Column<int>(type: "INTEGER", nullable: false),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OccurrenceContributors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OccurrenceContributors_EventOccurrences_EventOccurrenceId",
                        column: x => x.EventOccurrenceId,
                        principalTable: "EventOccurrences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OccurrenceContributors_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OccurrenceContributors_EventOccurrenceId_UserId",
                table: "OccurrenceContributors",
                columns: new[] { "EventOccurrenceId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OccurrenceContributors_UserId",
                table: "OccurrenceContributors",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OccurrenceContributors");

            migrationBuilder.DropColumn(
                name: "EventType",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "IsLightningRound",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "NamePrefix",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "NameSuffix",
                table: "EventOccurrences");
        }
    }
}
