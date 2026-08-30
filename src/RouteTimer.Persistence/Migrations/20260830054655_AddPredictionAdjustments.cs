using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPredictionAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prediction_adjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PredictionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StrategyType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StrategyJson = table.Column<string>(type: "jsonb", nullable: false),
                    StrategyAlgorithmVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MovingSeconds = table.Column<double>(type: "double precision", nullable: true),
                    AverageSpeedMetresPerSecond = table.Column<double>(type: "double precision", nullable: true),
                    AveragePowerWatts = table.Column<double>(type: "double precision", nullable: true),
                    Confidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Warnings = table.Column<string>(type: "jsonb", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_adjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_prediction_adjustments_predictions_PredictionId",
                        column: x => x.PredictionId,
                        principalTable: "predictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "prediction_adjustment_segments",
                columns: table => new
                {
                    AdjustmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    PowerWatts = table.Column<double>(type: "double precision", nullable: false),
                    SpeedMetresPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    SegmentMovingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeMovingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ZoneNumber = table.Column<int>(type: "integer", nullable: true),
                    StrategyPhase = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    WPrimeBalanceJoules = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_adjustment_segments", x => new { x.AdjustmentId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_prediction_adjustment_segments_prediction_adjustments_Adjus~",
                        column: x => x.AdjustmentId,
                        principalTable: "prediction_adjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prediction_adjustments_PredictionId_CreatedAt",
                table: "prediction_adjustments",
                columns: new[] { "PredictionId", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prediction_adjustment_segments");

            migrationBuilder.DropTable(
                name: "prediction_adjustments");
        }
    }
}
