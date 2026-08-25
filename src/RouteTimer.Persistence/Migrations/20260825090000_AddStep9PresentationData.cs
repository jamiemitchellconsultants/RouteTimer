using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStep9PresentationData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AscentMetres",
                table: "training_activities",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceManufacturer",
                table: "training_activities",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeviceProduct",
                table: "training_activities",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DistanceMetres",
                table: "training_activities",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EndedAt",
                table: "training_activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceFileName",
                table: "training_activities",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "training_activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "analysis_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProgressPercent",
                table: "analysis_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "ProgressStage",
                table: "analysis_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                table: "analysis_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "analysis_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE training_activities AS activity
                SET "SourceFileName" = upload."FileName"
                FROM stored_uploads AS upload
                WHERE upload."Id" = activity."UploadId";
                """);

            migrationBuilder.Sql("""
                UPDATE training_activities
                SET "SourceFileName" = "Name"
                WHERE "SourceFileName" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE analysis_jobs
                SET "UpdatedAt" = "CreatedAt",
                    "StartedAt" = CASE WHEN "State" IN ('Running','Succeeded','Failed') THEN "CreatedAt" ELSE NULL END,
                    "CompletedAt" = CASE WHEN "State" IN ('Succeeded','Failed') THEN "CreatedAt" ELSE NULL END,
                    "ProgressPercent" = CASE WHEN "State" = 'Succeeded' THEN 100 ELSE 0 END,
                    "ProgressStage" = CASE
                        WHEN "State" = 'Running' THEN 'running'
                        WHEN "State" = 'Succeeded' THEN 'completed'
                        WHEN "State" = 'Failed' THEN 'failed'
                        ELSE 'queued'
                    END;
                """);

            migrationBuilder.DropIndex(
                name: "IX_analysis_jobs_active_type_subject",
                table: "analysis_jobs");

            migrationBuilder.AlterColumn<string>(
                name: "SourceFileName",
                table: "training_activities",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ProgressStage",
                table: "analysis_jobs",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "analysis_jobs",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_analysis_jobs_queued_type_subject",
                table: "analysis_jobs",
                columns: new[] { "Type", "SubjectId" },
                unique: true,
                filter: "\"State\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_jobs_running_type_subject",
                table: "analysis_jobs",
                columns: new[] { "Type", "SubjectId" },
                unique: true,
                filter: "\"State\" = 'Running'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_analysis_jobs_progress",
                table: "analysis_jobs",
                sql: "\"ProgressPercent\" BETWEEN 0 AND 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_analysis_jobs_queued_type_subject",
                table: "analysis_jobs");

            migrationBuilder.DropIndex(
                name: "IX_analysis_jobs_running_type_subject",
                table: "analysis_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_analysis_jobs_progress",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "AscentMetres",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "DeviceManufacturer",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "DeviceProduct",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "DistanceMetres",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "SourceFileName",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "training_activities");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressPercent",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "ProgressStage",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                table: "analysis_jobs");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "analysis_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_jobs_active_type_subject",
                table: "analysis_jobs",
                columns: new[] { "Type", "SubjectId" },
                unique: true,
                filter: "\"State\" IN ('Queued', 'Running')");
        }
    }
}
