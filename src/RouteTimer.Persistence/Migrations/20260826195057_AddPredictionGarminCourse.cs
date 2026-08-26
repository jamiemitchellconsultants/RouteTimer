using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionGarminCourse : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GarminCourseId",
                table: "predictions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "GarminCourseUploadedAt",
                table: "predictions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GarminCourseId",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "GarminCourseUploadedAt",
                table: "predictions");
        }
    }
}
