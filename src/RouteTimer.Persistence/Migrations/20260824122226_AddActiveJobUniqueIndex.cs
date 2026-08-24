using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddActiveJobUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_analysis_jobs_active_type_subject",
                table: "analysis_jobs",
                columns: new[] { "Type", "SubjectId" },
                unique: true,
                filter: "\"State\" IN ('Queued', 'Running')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_analysis_jobs_active_type_subject",
                table: "analysis_jobs");
        }
    }
}
