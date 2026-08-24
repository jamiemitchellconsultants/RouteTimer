using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDurablePredictions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM predictions) THEN
                        RAISE EXCEPTION 'legacy-predictions-not-supported: existing placeholder predictions cannot be upgraded without a retained GPX and model snapshot.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AddColumn<double>(
                name: "AscentMetres",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AssumptionMovingOnly",
                table: "predictions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AssumptionSurface",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssumptionWeather",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssumptionWind",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<double>(
                name: "AveragePowerWatts",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AverageSpeedMetresPerSecond",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "predictions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Confidence",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DistanceMetres",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ModelValidationMedianApe",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ModelValidationP90Ape",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelValidationStatus",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "ModelWasCalibrated",
                table: "predictions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "MovingSeconds",
                table: "predictions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RiderModelId",
                table: "predictions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "predictions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "UploadId",
                table: "predictions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Warnings",
                table: "predictions",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.CreateTable(
                name: "prediction_segments",
                columns: table => new
                {
                    PredictionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Latitude = table.Column<double>(type: "double precision", nullable: false),
                    Longitude = table.Column<double>(type: "double precision", nullable: false),
                    ElevationMetres = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeDistanceMetres = table.Column<double>(type: "double precision", nullable: false),
                    SegmentDistanceMetres = table.Column<double>(type: "double precision", nullable: false),
                    Gradient = table.Column<double>(type: "double precision", nullable: false),
                    CurvaturePerMetre = table.Column<double>(type: "double precision", nullable: false),
                    PredictedPowerWatts = table.Column<double>(type: "double precision", nullable: false),
                    PredictedSpeedMetresPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    SegmentMovingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    CumulativeMovingSeconds = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prediction_segments", x => new { x.PredictionId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_prediction_segments_predictions_PredictionId",
                        column: x => x.PredictionId,
                        principalTable: "predictions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_predictions_RiderModelId",
                table: "predictions",
                column: "RiderModelId");

            migrationBuilder.CreateIndex(
                name: "IX_predictions_UploadId",
                table: "predictions",
                column: "UploadId");

            migrationBuilder.AddForeignKey(
                name: "FK_predictions_rider_models_RiderModelId",
                table: "predictions",
                column: "RiderModelId",
                principalTable: "rider_models",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_predictions_stored_uploads_UploadId",
                table: "predictions",
                column: "UploadId",
                principalTable: "stored_uploads",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_predictions_rider_models_RiderModelId",
                table: "predictions");

            migrationBuilder.DropForeignKey(
                name: "FK_predictions_stored_uploads_UploadId",
                table: "predictions");

            migrationBuilder.DropTable(
                name: "prediction_segments");

            migrationBuilder.DropIndex(
                name: "IX_predictions_RiderModelId",
                table: "predictions");

            migrationBuilder.DropIndex(
                name: "IX_predictions_UploadId",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AscentMetres",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AssumptionMovingOnly",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AssumptionSurface",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AssumptionWeather",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AssumptionWind",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AveragePowerWatts",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "AverageSpeedMetresPerSecond",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "Confidence",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "DistanceMetres",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "ModelValidationMedianApe",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "ModelValidationP90Ape",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "ModelValidationStatus",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "ModelWasCalibrated",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "MovingSeconds",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "RiderModelId",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "State",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "UploadId",
                table: "predictions");

            migrationBuilder.DropColumn(
                name: "Warnings",
                table: "predictions");
        }
    }
}
