using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleOfficeScheduler.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RoomDisplayName",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomEmail",
                table: "Events",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomBookingError",
                table: "EventOccurrences",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RoomBookingStatus",
                table: "EventOccurrences",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RoomDisplayName",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RoomEmail",
                table: "Events");

            migrationBuilder.DropColumn(
                name: "RoomBookingError",
                table: "EventOccurrences");

            migrationBuilder.DropColumn(
                name: "RoomBookingStatus",
                table: "EventOccurrences");
        }
    }
}
