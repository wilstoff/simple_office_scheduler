using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleOfficeScheduler.Migrations
{
    /// <inheritdoc />
    public partial class RenameLightningRoundToTalksAndAddCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsLightningRound",
                table: "EventOccurrences",
                newName: "IsLightningTalks");

            migrationBuilder.AddColumn<int>(
                name: "LightningTalksCapacity",
                table: "EventOccurrences",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LightningTalksCapacity",
                table: "EventOccurrences");

            migrationBuilder.RenameColumn(
                name: "IsLightningTalks",
                table: "EventOccurrences",
                newName: "IsLightningRound");
        }
    }
}
