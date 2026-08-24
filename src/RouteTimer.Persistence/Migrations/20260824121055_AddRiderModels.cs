using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRiderModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "rider_models",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProfileRiderWeightKg = table.Column<double>(type: "double precision", nullable: false),
                    ProfileBikeWeightKg = table.Column<double>(type: "double precision", nullable: false),
                    AlgorithmVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DrivetrainEfficiency = table.Column<double>(type: "double precision", nullable: false),
                    AirDensity = table.Column<double>(type: "double precision", nullable: false),
                    Crr = table.Column<double>(type: "double precision", nullable: false),
                    CdA = table.Column<double>(type: "double precision", nullable: false),
                    WasCalibrated = table.Column<bool>(type: "boolean", nullable: false),
                    GlobalTypicalWatts = table.Column<double>(type: "double precision", nullable: false),
                    ValidationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ValidationMedianApe = table.Column<double>(type: "double precision", nullable: true),
                    ValidationP90Ape = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rider_models", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "power_bands",
                columns: table => new
                {
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    GradeKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DurationKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TypicalWatts = table.Column<double>(type: "double precision", nullable: false),
                    EvidenceSeconds = table.Column<double>(type: "double precision", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    ShrinkageWeight = table.Column<double>(type: "double precision", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_power_bands", x => new { x.ModelId, x.GradeKey, x.DurationKey });
                    table.ForeignKey(
                        name: "FK_power_bands_rider_models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "rider_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_rider_models_CreatedAt",
                table: "rider_models",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "power_bands");

            migrationBuilder.DropTable(
                name: "rider_models");
        }
    }
}
