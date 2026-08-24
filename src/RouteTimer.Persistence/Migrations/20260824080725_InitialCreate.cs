using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations;

public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "predictions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ModelVersion = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RiderWeightKg = table.Column<double>(type: "double precision", nullable: false),
                BikeWeightKg = table.Column<double>(type: "double precision", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_predictions", x => x.Id));

        migrationBuilder.CreateTable(
            name: "stored_uploads",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                Content = table.Column<byte[]>(type: "bytea", nullable: false),
                Sha256 = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_stored_uploads", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_predictions_CreatedAt", table: "predictions", column: "CreatedAt");
        migrationBuilder.CreateIndex(name: "IX_stored_uploads_Kind_Sha256", table: "stored_uploads", columns: ["Kind", "Sha256"], unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "predictions");
        migrationBuilder.DropTable(name: "stored_uploads");
    }
}
