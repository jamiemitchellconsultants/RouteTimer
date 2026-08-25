using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RouteTimer.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGarminActivityImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "garmin_activity_imports",
                columns: table => new
                {
                    GarminActivityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActivityName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garmin_activity_imports", x => x.GarminActivityId);
                    table.ForeignKey(
                        name: "FK_garmin_activity_imports_stored_uploads_UploadId",
                        column: x => x.UploadId,
                        principalTable: "stored_uploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "garmin_connections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    GarminUserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EncryptionVersion = table.Column<int>(type: "integer", nullable: false),
                    Nonce = table.Column<byte[]>(type: "bytea", nullable: false),
                    Ciphertext = table.Column<byte[]>(type: "bytea", nullable: false),
                    Tag = table.Column<byte[]>(type: "bytea", nullable: false),
                    LastValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_garmin_connections", x => x.Id);
                    table.CheckConstraint("CK_garmin_connections_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_garmin_activity_imports_UploadId",
                table: "garmin_activity_imports",
                column: "UploadId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "garmin_activity_imports");

            migrationBuilder.DropTable(
                name: "garmin_connections");
        }
    }
}
