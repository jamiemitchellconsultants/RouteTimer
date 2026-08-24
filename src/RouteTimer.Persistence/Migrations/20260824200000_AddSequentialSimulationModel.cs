using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSequentialSimulationModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DescentWasLearned",
                table: "rider_models",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "CurvaturePerMetre",
                table: "activity_samples",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.CreateTable(
                name: "rider_model_descent_limits",
                columns: table => new
                {
                    ModelId = table.Column<Guid>(type: "uuid", nullable: false),
                    GradeKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CurvatureKey = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SpeedCapMetresPerSecond = table.Column<double>(type: "double precision", nullable: false),
                    EvidenceSeconds = table.Column<double>(type: "double precision", nullable: false),
                    ActivityCount = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsFallback = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rider_model_descent_limits", x => new { x.ModelId, x.GradeKey, x.CurvatureKey });
                    table.ForeignKey(
                        name: "FK_rider_model_descent_limits_rider_models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "rider_models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO rider_model_descent_limits
                    ("ModelId", "GradeKey", "CurvatureKey", "SpeedCapMetresPerSecond", "EvidenceSeconds", "ActivityCount", "Confidence", "IsFallback")
                SELECT
                    model."Id",
                    grade."GradeKey",
                    curvature."CurvatureKey",
                    LEAST(grade."SpeedCapMetresPerSecond", curvature."SpeedCapMetresPerSecond"),
                    0.0,
                    0,
                    'Low',
                    TRUE
                FROM rider_models AS model
                CROSS JOIN (VALUES
                    ('mild', 13.0),
                    ('medium', 16.0),
                    ('steep', 18.0)
                ) AS grade("GradeKey", "SpeedCapMetresPerSecond")
                CROSS JOIN (VALUES
                    ('straight', 20.0),
                    ('moderate', 31.622776601683793),
                    ('tight', 14.142135623730951)
                ) AS curvature("CurvatureKey", "SpeedCapMetresPerSecond");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rider_model_descent_limits");

            migrationBuilder.DropColumn(
                name: "DescentWasLearned",
                table: "rider_models");

            migrationBuilder.DropColumn(
                name: "CurvaturePerMetre",
                table: "activity_samples");
        }
    }
}
