using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTrainingActivities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "training_activities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    MovingDurationSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Eligibility = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PositionCoverage = table.Column<double>(type: "double precision", nullable: false),
                    ElevationCoverage = table.Column<double>(type: "double precision", nullable: false),
                    SpeedCoverage = table.Column<double>(type: "double precision", nullable: false),
                    PowerCoverage = table.Column<double>(type: "double precision", nullable: false),
                    ExclusionCounts = table.Column<string>(type: "jsonb", nullable: false),
                    ReasonCodes = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_training_activities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "activity_samples",
                columns: table => new
                {
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MovingElapsedSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    ElevationMetres = table.Column<double>(type: "double precision", nullable: false),
                    SpeedMetresPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    PowerWatts = table.Column<int>(type: "integer", nullable: true),
                    HeartRate = table.Column<byte>(type: "smallint", nullable: true),
                    Cadence = table.Column<byte>(type: "smallint", nullable: true),
                    CrossesDiscontinuity = table.Column<bool>(type: "boolean", nullable: false),
                    Gradient = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_activity_samples", x => new { x.ActivityId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_activity_samples_training_activities_ActivityId",
                        column: x => x.ActivityId,
                        principalTable: "training_activities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_training_activities_UploadId",
                table: "training_activities",
                column: "UploadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "activity_samples");

            migrationBuilder.DropTable(
                name: "training_activities");
        }
    }
}
