using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrityConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes",
                sql: "remaining_visits >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_enrollments_seats",
                table: "enrollments",
                sql: "seats >= 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_passes_user_id",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_passes_remaining_visits",
                table: "passes");

            migrationBuilder.DropCheckConstraint(
                name: "ck_enrollments_seats",
                table: "enrollments");

            migrationBuilder.CreateIndex(
                name: "IX_passes_user_id",
                table: "passes",
                column: "user_id");
        }
    }
}
